using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Bounded read-only sale details. Caller owns a consistent read or writer transaction.</summary>
internal static partial class SaleDocumentReader
{
    internal const string Unavailable = "Desglose no verificable o fuera del límite de lectura. Conserve el folio y solicite conciliación; no imprima ni devuelva mercancía con este detalle.";
    internal const string Deferred = "Desglose pendiente de consulta individual por el volumen de la página. Abra Ver detalle.";
    private static readonly string[] TextColumns = ["id", "operation_id", "date", "paymentMethod", "giftcard_number", "customerId", "cancelledAt",
        "invoice_id", "invoice_status", "estado_fiscal", "uuid_sat", "serie_folio", "cancellation_reason", "cancellation_date", "committed_at_utc",
        "ShiftId", "RegisterId", "CashierUserId", "CashierName"];
    private static string Bytes(string expression) => $"length(CAST(COALESCE({expression}, '') AS BLOB))";
    private static string Invalid(string expression, int bytes = 8192) => $"({Bytes(expression)} > {bytes} OR instr(COALESCE({expression}, ''), char(0)) > 0)";
    internal static readonly string SafeDate = $"CASE WHEN NOT {Invalid("date", 128)} THEN date ELSE '' END";
    private static readonly string HeaderSql = "SELECT " + string.Join(", ", TextColumns.Select(c =>
        $"CASE WHEN NOT {Invalid($"s.\"{c}\"")} THEN s.\"{c}\" ELSE '' END AS \"{c}\"")) +
        ", '[]' AS items, s.total, s.totalCost, s.profit, s.paidAmount, s.changeAmount, s.cashAmount, s.cardAmount, s.giftcard_amount," +
        " s.isCredit, s.isCancelled, s.status, s.LedgerVersion FROM sales s";
    internal static Task<List<SaleEntity>> HeadersAsync(AppDbContext db, FormattableString selection) =>
        db.Sales.FromSql(FormattableStringFactory.Create(HeaderSql + " WHERE s.rowid IN (" + selection.Format +
            $") ORDER BY ticketfy_business_ticks({SafeDate}) DESC, s.id COLLATE BINARY", selection.GetArguments())).TagWith("Sale history headers").AsNoTracking().ToListAsync();
    internal static Task<SaleEntity?> HeaderAsync(AppDbContext db, string key, bool operation = false) =>
        db.Sales.FromSql(FormattableStringFactory.Create(HeaderSql + (operation ? " WHERE s.operation_id = {0}" : " WHERE s.id = {0}"), key))
            .TagWith("Sale document header").AsNoTracking().SingleOrDefaultAsync();

    private sealed class Footprint
    {
        public string Id { get; set; } = "";
        public long Bytes { get; set; }
        public long Lines { get; set; }
        public long Payments { get; set; }
        public bool Invalid { get; set; }
    }
    private static string SumBytes(string table, string alias, string[] fields) =>
        $"(SELECT COALESCE(SUM({string.Join(" + ", fields.Select(c => Bytes($"{alias}.\"{c}\"")))}), 0) FROM {table} {alias} WHERE {alias}.\"SaleId\" = s.id)";
    private static string InvalidRows(string table, string alias, string[] fields) =>
        $"EXISTS(SELECT 1 FROM {table} {alias} WHERE {alias}.\"SaleId\" = s.id AND ({string.Join(" OR ", fields.Select(c => Invalid($"{alias}.\"{c}\"")))}))";
    private static readonly string[] LineText = ["Id", "SaleId", "ProductId", "Name", "Unit", "PromotionId"];
    private static readonly string[] PaymentText = ["Id", "SaleId", "OperationId", "ExternalReference", "AccountId", "ShiftId", "RegisterId", "CashierUserId", "CreatedAtUtc"];
    private static readonly string FootprintSql = "SELECT s.id AS Id, " +
        string.Join(" + ", TextColumns.Select(c => Bytes($"s.\"{c}\""))) + " + " + Bytes("s.items") + " + " +
        SumBytes("sale_lines", "l", LineText) + " + " + SumBytes("sale_payments", "p", PaymentText) + " AS Bytes, " +
        "(SELECT COUNT(*) FROM sale_lines l WHERE l.SaleId = s.id) AS Lines, (SELECT COUNT(*) FROM sale_payments p WHERE p.SaleId = s.id) AS Payments, (" +
        string.Join(" OR ", TextColumns.Select(c => Invalid($"s.\"{c}\""))) + " OR " + Invalid("s.items", CommercialDocumentLimits.PayloadBytes) +
        " OR " + InvalidRows("sale_lines", "l", LineText) + " OR " + InvalidRows("sale_payments", "p", PaymentText) + ") AS Invalid FROM sales s";

    internal static async Task<List<SaleDto>> ReadAsync(AppDbContext db, List<SaleEntity> headers, bool costsVisible)
    {
        if (headers.Count == 0) return [];
        var ids = headers.Select(s => s.Id).ToArray();
        var footprints = await db.Database.SqlQueryRaw<Footprint>(FootprintSql).Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id);
        var accepted = new List<string>(); var warnings = new Dictionary<string, string>();
        long bytes = 0, lineCount = 0;
        foreach (var s in headers)
        {
            if (!footprints.TryGetValue(s.Id, out var f) || f.Invalid || f.Lines is < 1 or > CommercialDocumentLimits.Lines ||
                f.Payments > CommercialDocumentLimits.Tenders || f.Bytes > CommercialDocumentLimits.PayloadBytes + CommercialDocumentLimits.TotalTextBytes)
            { warnings[s.Id] = Unavailable; continue; }
            if (bytes + f.Bytes > 8 * 1024 * 1024 || lineCount + f.Lines > 10000)
            { warnings[s.Id] = Deferred; continue; }
            bytes += f.Bytes; lineCount += f.Lines; accepted.Add(s.Id);
        }
        var payloads = await db.Sales.AsNoTracking().Where(s => accepted.Contains(s.Id)).Select(s => new { s.Id, s.ItemsJson }).ToDictionaryAsync(s => s.Id, s => s.ItemsJson);
        var lines = (await db.SaleLines.AsNoTracking().Where(l => accepted.Contains(l.SaleId)).OrderBy(l => l.LineNumber).ToListAsync()).ToLookup(l => l.SaleId);
        var payments = (await db.SalePayments.AsNoTracking().Where(p => accepted.Contains(p.SaleId)).ToListAsync()).ToLookup(p => p.SaleId);
        var returns = new Dictionary<string, (decimal Qty, decimal Amount)>();
        await foreach (var r in (from r in db.ReturnLines.AsNoTracking()
                                 join l in db.SaleLines.AsNoTracking() on r.SaleLineId equals l.Id
                                 where accepted.Contains(l.SaleId)
                                 select new { l.SaleId, l.Id, r.Quantity, r.Amount }).AsAsyncEnumerable())
        {
            if (r.Quantity <= 0 || Math.Round(r.Quantity, 3) != r.Quantity || r.Amount < 0) warnings[r.SaleId] = Unavailable;
            var old = returns.GetValueOrDefault(r.Id);
            try { returns[r.Id] = (old.Qty + r.Quantity, old.Amount + r.Amount); }
            catch (OverflowException) { warnings[r.SaleId] = Unavailable; }
        }
        var credit = new Dictionary<string, decimal>();
        await foreach (var e in (from e in db.AccountEntries.AsNoTracking()
                                 join s in db.Sales.AsNoTracking() on e.DocumentId equals s.Id
                                 where accepted.Contains(s.Id) && e.AccountKind == AccountKind.Customer
                                 select new { s.Id, e.Amount, Valid = e.AccountId == s.CustomerId }).AsAsyncEnumerable())
        {
            if (!e.Valid) warnings[e.Id] = Unavailable;
            try { credit[e.Id] = credit.GetValueOrDefault(e.Id) + e.Amount; }
            catch (OverflowException) { warnings[e.Id] = Unavailable; }
        }
        var result = new List<SaleDto>();
        foreach (var s in headers)
        {
            if (warnings.TryGetValue(s.Id, out var warning)) { result.Add(Map(s, [], [], 0m, costsVisible, null, warning)); continue; }
            try
            {
                var items = JsonSerializer.Deserialize(payloads[s.Id], Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto) ?? [];
                var source = lines[s.Id].ToList(); var tenders = payments[s.Id].ToList();
                Validate(s, items, source, tenders);
                decimal refunded = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var returned = returns.GetValueOrDefault(source[i].Id);
                    if (returned.Qty > source[i].Quantity || returned.Amount != CommercialLedger.Money(source[i].Total * returned.Qty / source[i].Quantity))
                        throw new InvalidOperationException(Unavailable);
                    refunded += returned.Amount;
                    items[i] = items[i] with { SaleLineId = source[i].Id, ReturnedQuantity = (double)returned.Qty, Cost = costsVisible ? items[i].Cost : 0m };
                }
                result.Add(Map(s, items, tenders.Select(p => new SaleTenderDto(p.Kind, p.Amount, p.ExternalReference, p.AccountId)).ToList(),
                    refunded, costsVisible, s.IsCredit == 1 && credit.TryGetValue(s.Id, out var balance) ? balance : null));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
            { result.Add(Map(s, [], [], 0m, costsVisible, null, Unavailable)); }
        }
        return result;
    }

    private static void Validate(SaleEntity s, List<SaleItemSnapshotDto> items, List<SaleLineEntity> lines, List<SalePaymentEntity> payments)
    {
        CommercialDocumentLimits.ValidateSale(Map(s, items, payments.Select(p => new SaleTenderDto(p.Kind, p.Amount, p.ExternalReference, p.AccountId)).ToList(), 0m, true, null));
        if (s.LedgerVersion != 1 || s.Total < 0 || s.TotalCost < 0 || s.Profit != s.Total - s.TotalCost ||
            string.IsNullOrWhiteSpace(s.OperationId) || string.IsNullOrWhiteSpace(s.ShiftId) || string.IsNullOrWhiteSpace(s.RegisterId) || string.IsNullOrWhiteSpace(s.CashierUserId) ||
            !Ticketfy.Core.Helpers.BusinessTime.TryLocal(s.Date, out _) ||
            items.Count != lines.Count || lines.Sum(l => l.Total) != s.Total || lines.Sum(l => l.CostTotal) != s.TotalCost ||
            payments.Sum(p => p.Amount) != s.Total || payments.Select(p => p.Kind).Distinct().Count() != payments.Count ||
            s.CashAmount != payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount) ||
            s.CardAmount != payments.Where(p => p.Kind == TenderKind.Card).Sum(p => p.Amount) ||
            s.GiftcardAmount != payments.Where(p => p.Kind == TenderKind.Giftcard).Sum(p => p.Amount))
            throw new InvalidOperationException(Unavailable);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i]; var line = lines[i];
            if (line.LineNumber != i + 1 || line.Quantity <= 0 || Math.Round(line.Quantity, 3) != line.Quantity ||
                !double.IsFinite(item.Quantity) || (decimal)item.Quantity != line.Quantity || item.ProductId != line.ProductId ||
                item.SaleLineId is not null && item.SaleLineId != line.Id || item.TotalPrice != line.Total || line.Total < 0 ||
                item.Name != line.Name || item.Unit != line.Unit || CommercialLedger.Money(item.Cost) != line.UnitCost ||
                CommercialLedger.Money(item.OriginalUnitPrice) != line.UnitPrice || item.TaxAmount != line.Tax || line.Tax < 0 || line.Tax > line.Total ||
                item.AppliedDiscountAmount + item.ProratedGlobalDiscountAmount != line.Discount || line.Discount < 0 ||
                CommercialLedger.Money(item.Cost * line.Quantity) != line.CostTotal || line.CostTotal < 0)
                throw new InvalidOperationException(Unavailable);
        }
        if (payments.Any(p => p.Amount <= 0 || p.State != TenderState.Captured || !Enum.IsDefined(p.Kind) ||
            p.OperationId != s.OperationId || p.ShiftId != s.ShiftId || p.RegisterId != s.RegisterId || p.CashierUserId != s.CashierUserId))
            throw new InvalidOperationException(Unavailable);
    }

    private static SaleDto Map(SaleEntity s, List<SaleItemSnapshotDto> items, List<SaleTenderDto> payments, decimal refunded,
        bool visible, decimal? credit, string? warning = null) => new(s.Id, s.Date, items, s.Total, visible ? s.TotalCost : 0m,
            visible ? s.Profit : 0m, s.PaidAmount, s.ChangeAmount, s.PaymentMethod, s.CustomerId, s.IsCredit == 1, s.IsCancelled == 1,
            s.CancelledAt, s.EstadoFiscal, s.UuidSat, s.SerieFolio, s.Status, s.InvoiceId, s.InvoiceStatus, s.CashierUserId, s.CashierName,
            s.CashAmount, s.CardAmount, s.OperationId, s.GiftcardNumber, s.GiftcardAmount, s.ShiftId, s.RegisterId,
            Payments: payments, RefundedAmount: refunded)
        { CostsVisible = visible, CreditDocumentBalance = credit, DetailsAvailable = warning is null, DetailWarning = warning ?? "" };
}
