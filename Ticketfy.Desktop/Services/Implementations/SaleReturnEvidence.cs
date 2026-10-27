using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Original evidence and streamed compensation totals, read inside the caller's writer transaction.</summary>
internal sealed partial class SaleReturnEvidence
{
    internal const string Unverifiable = "La venta, sus pagos o sus devoluciones no tienen evidencia coherente. Conserve el folio y solicite conciliación; no entregue dinero ni mueva mercancía.";
    internal required SaleEntity Sale { get; init; }
    internal required List<SaleLineEntity> Lines { get; init; }
    internal required List<SalePaymentEntity> Payments { get; init; }
    internal required List<SaleItemSnapshotDto> Items { get; init; }
    internal required ILookup<string?, InventoryMovementEntity> Stock { get; init; }
    internal Dictionary<string, LineTotals> Returned { get; } = [];
    internal Dictionary<string, decimal> Refunded { get; } = [];
    internal sealed class LineTotals
    {
        internal decimal Quantity, Amount, Restocked, Cost;
    }

    internal static void Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) Fail();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Fail() => throw new InvalidOperationException(Unverifiable);
    private static bool Money(decimal value) => value >= 0 && CommercialLedger.Money(value) == value;
    private static bool Quantity(decimal value) => value > 0 && Math.Round(value, 3) == value;
    private static bool Actor(string? user, string? register, string? shift, SaleEntity sale) =>
        user == sale.CashierUserId && register == sale.RegisterId && shift == sale.ShiftId;

    private sealed class Budget
    {
        public long Rows { get; set; }
        public long Bytes { get; set; }
        public long Invalid { get; set; }
    }
    private static string ByteLength(string column) => $"length(CAST(COALESCE(\"{column}\", '') AS BLOB))";
    // Table, predicate and column names below are code-owned, never input or metadata supplied by a caller.
    // Values stay EF parameters. Check bytes and NUL before the provider materializes any strings.
    private static async Task<long> BudgetAsync(AppDbContext db, string table, string predicate, string[] columns,
        long maximumRows, long maximumBytes, string id, string? payload = null)
    {
        var bytes = columns.Select(ByteLength).ToList();
        if (payload is not null) bytes.Add(ByteLength(payload));
        var invalid = columns.Select(c => $"{ByteLength(c)} > 8192 OR instr(COALESCE(\"{c}\", ''), char(0)) > 0").ToList();
        if (payload is not null)
            invalid.Add($"{ByteLength(payload)} > {CommercialDocumentLimits.PayloadBytes} OR instr(COALESCE(\"{payload}\", ''), char(0)) > 0");
        var sql = $"SELECT COUNT(*) AS Rows, COALESCE(SUM({string.Join(" + ", bytes)}), 0) AS Bytes, " +
            $"COALESCE(MAX(CASE WHEN {string.Join(" OR ", invalid)} THEN 1 ELSE 0 END), 0) AS Invalid FROM \"{table}\" WHERE {predicate}";
        var budget = await db.Database.SqlQuery<Budget>(FormattableStringFactory.Create(sql, id)).SingleAsync();
        if (budget.Rows > maximumRows || budget.Bytes > maximumBytes || budget.Invalid != 0) Fail();
        return budget.Rows;
    }

    internal static async Task<SaleReturnEvidence?> ReadAsync(AppDbContext db, string id)
    {
        Key(id);
        if (await BudgetAsync(db, "sales", "id = {0}",
            ["id", "operation_id", "date", "paymentMethod", "giftcard_number", "customerId", "cancelledAt", "invoice_id",
             "invoice_status", "estado_fiscal", "uuid_sat", "serie_folio", "cancellation_reason", "cancellation_date",
             "committed_at_utc", "ShiftId", "RegisterId", "CashierUserId", "CashierName"],
            1, CommercialDocumentLimits.PayloadBytes + CommercialDocumentLimits.TotalTextBytes, id, "items") == 0) return null;
        var sale = await db.Sales.SingleAsync(s => s.Id == id);
        if (sale.Status is SaleStatus.Canceled or SaleStatus.Refunded) return null;
        if (sale.LedgerVersion != 1 || sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded)) Fail();
        Key(sale.Id); Key(sale.OperationId); Key(sale.CashierUserId); Key(sale.RegisterId); Key(sale.ShiftId);
        if (!Money(sale.Total) || !Money(sale.TotalCost) || sale.Profit != sale.Total - sale.TotalCost) Fail();

        await BudgetAsync(db, "sale_lines", "\"SaleId\" = {0}",
            ["Id", "SaleId", "ProductId", "Name", "Unit", "PromotionId"],
            CommercialDocumentLimits.Lines, CommercialDocumentLimits.TotalTextBytes, id);
        await BudgetAsync(db, "sale_payments", "\"SaleId\" = {0}",
            ["Id", "SaleId", "OperationId", "ExternalReference", "AccountId", "ShiftId", "RegisterId", "CashierUserId", "CreatedAtUtc"],
            CommercialDocumentLimits.Tenders, CommercialDocumentLimits.TotalTextBytes, id);
        var lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == id).OrderBy(l => l.LineNumber).ToListAsync();
        var payments = await db.SalePayments.AsNoTracking().Where(p => p.SaleId == id).OrderBy(p => p.Kind).ToListAsync();
        List<SaleItemSnapshotDto> items;
        try
        {
            items = JsonSerializer.Deserialize(CommercialDocumentLimits.RequirePayload(sale.ItemsJson),
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto) ?? [];
            CommercialDocumentLimits.ValidateSale(new(sale.Id, sale.Date, items, sale.Total, sale.TotalCost, sale.Profit,
                sale.PaidAmount, sale.ChangeAmount, sale.PaymentMethod, sale.CustomerId, sale.IsCredit == 1, false, null));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { throw new InvalidOperationException(Unverifiable, ex); }
        if (lines.Count != items.Count || lines.Sum(l => l.Total) != sale.Total ||
            lines.Sum(l => l.CostTotal) != sale.TotalCost) Fail();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i]; var item = items[i];
            Key(l.Id); Key(l.ProductId);
            if (l.LineNumber != i + 1 || !Quantity(l.Quantity) || !Money(l.Total) || !Money(l.CostTotal) ||
                !Money(l.UnitCost) || !Money(l.UnitPrice) || !Money(l.Discount) || !Money(l.Tax) || l.Tax > l.Total ||
                !double.IsFinite(item.Quantity) || (decimal)item.Quantity != l.Quantity || item.SaleLineId != l.Id ||
                item.ProductId != l.ProductId || item.Name != l.Name || item.Unit != l.Unit ||
                item.TotalPrice != l.Total || item.Cost < 0 || CommercialLedger.Money(item.Cost) != l.UnitCost ||
                CommercialLedger.Money(item.Cost * l.Quantity) != l.CostTotal ||
                CommercialLedger.Money(item.OriginalUnitPrice) != l.UnitPrice || item.TaxAmount != l.Tax ||
                !double.IsFinite(item.PointsRewarded) || item.PointsRewarded < 0) Fail();
        }
        if (payments.Select(p => p.Kind).Distinct().Count() != payments.Count || payments.Sum(p => p.Amount) != sale.Total) Fail();
        foreach (var p in payments)
        {
            Key(p.Id);
            if (!Enum.IsDefined(p.Kind) || p.State != TenderState.Captured || p.Amount <= 0 || !Money(p.Amount) ||
                p.OperationId != sale.OperationId || !Actor(p.CashierUserId, p.RegisterId, p.ShiftId, sale)) Fail();
            if (p.Kind is TenderKind.Card or TenderKind.Transfer &&
                (string.IsNullOrWhiteSpace(p.ExternalReference) || p.ExternalReference.Length > 200 || p.ExternalReference.Any(char.IsControl))) Fail();
            if (p.Kind is TenderKind.Credit or TenderKind.Loyalty)
            {
                Key(p.AccountId);
                if (p.AccountId != sale.CustomerId || !await db.Customers.AnyAsync(c => c.Id == p.AccountId)) Fail();
            }
            if (p.Kind == TenderKind.Giftcard)
            {
                Key(p.AccountId);
                if (!await db.Giftcards.AnyAsync(g => g.CardNumber == p.AccountId)) Fail();
            }
        }
        if (sale.CashAmount != payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount) ||
            sale.CardAmount != payments.Where(p => p.Kind == TenderKind.Card).Sum(p => p.Amount) ||
            sale.GiftcardAmount != payments.Where(p => p.Kind == TenderKind.Giftcard).Sum(p => p.Amount)) Fail();
        const string originalStock = "\"Reason\" = 'SALE' AND (\"DocumentId\" = {0} OR \"SaleLineId\" IN (SELECT \"Id\" FROM sale_lines WHERE \"SaleId\" = {0}))";
        await BudgetAsync(db, "inventory_movements", originalStock,
            ["Id", "ProductId", "OperationId", "DocumentId", "SaleLineId", "Reason", "ShiftId", "RegisterId", "CashierUserId", "CreatedAtUtc"],
            CommercialDocumentLimits.StockMovements, 8 * 1024 * 1024, id);
        var lineIds = lines.Select(l => l.Id).ToArray();
        var movements = await db.InventoryMovements.AsNoTracking().Where(m => m.Reason == "SALE" &&
            (m.DocumentId == id || lineIds.Contains(m.SaleLineId!))).ToListAsync();
        foreach (var m in movements)
        {
            Key(m.Id); Key(m.ProductId);
            if (m.SaleLineId is null || !lineIds.Contains(m.SaleLineId) || m.DocumentId != id || m.OperationId != sale.OperationId ||
                !Quantity(-m.Quantity) || !Money(m.UnitCost) || m.StockBefore < 0 || m.StockAfter < 0 ||
                m.StockAfter != m.StockBefore + m.Quantity || !Actor(m.CashierUserId, m.RegisterId, m.ShiftId, sale)) Fail();
        }
        var stock = movements.ToLookup(m => m.SaleLineId);
        foreach (var line in lines)
        {
            var original = stock[line.Id].ToList();
            if (original.Count == 0 || original.Select(m => m.ProductId).Distinct().Count() != original.Count) Fail();
            if (original.Count == 1 && original[0].ProductId == line.ProductId && -original[0].Quantity != line.Quantity) Fail();
        }
        var result = new SaleReturnEvidence { Sale = sale, Lines = lines, Payments = payments, Items = items, Stock = stock };
        await result.ReadHistoryAsync(db);
        return result;
    }
}
