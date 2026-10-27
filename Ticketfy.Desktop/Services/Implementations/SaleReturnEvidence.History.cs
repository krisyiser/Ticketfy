using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal sealed partial class SaleReturnEvidence
{
    internal decimal RewardEarned { get; private set; }
    internal decimal RewardReversed { get; private set; }

    private async Task ReadHistoryAsync(AppDbContext db)
    {
        string id = Sale.Id;
        var lines = Lines.ToDictionary(l => l.Id);
        var lineIds = lines.Keys.ToArray();
        var payments = Payments.ToDictionary(p => p.Id);
        var paymentIds = payments.Keys.ToArray();
        var returnIds = db.Returns.Where(r => r.OriginalSaleId == id).Select(r => r.Id);
        const string returnScope = "original_sale_id = {0}";
        await BudgetAsync(db, "returns", returnScope,
            ["id", "original_sale_id", "operation_id", "cashier_user_id", "ShiftId", "RegisterId"],
            long.MaxValue, long.MaxValue, id);
        await BudgetAsync(db, "return_lines",
            "\"SaleLineId\" IN (SELECT \"Id\" FROM sale_lines WHERE \"SaleId\" = {0}) OR \"ReturnId\" IN (SELECT id FROM returns WHERE original_sale_id = {0})",
            ["Id", "SaleLineId", "ProductId", "ReturnId"], long.MaxValue, long.MaxValue, id);
        await BudgetAsync(db, "payment_refunds",
            "\"PaymentId\" IN (SELECT \"Id\" FROM sale_payments WHERE \"SaleId\" = {0}) OR \"ReturnId\" IN (SELECT id FROM returns WHERE original_sale_id = {0})",
            ["Id", "PaymentId", "ReturnId", "OperationId", "ShiftId", "RegisterId", "CashierUserId"],
            long.MaxValue, long.MaxValue, id);

        // A historical note or bank reference is not needed to validate a compensation.
        // Stream one return group at a time; keep only bounded totals for original lines/payments.
        string? group = null;
        decimal groupAmount = 0, groupCost = 0, expectedAmount = 0, expectedCost = 0;
        void FlushLines()
        {
            if (group is not null && (groupAmount != expectedAmount || groupCost != expectedCost)) Fail();
        }
        var returned = from l in db.ReturnLines.AsNoTracking()
                       join r in db.Returns.AsNoTracking() on l.ReturnId equals r.Id into parents
                       from r in parents.DefaultIfEmpty()
                       where lineIds.Contains(l.SaleLineId) || returnIds.Contains(l.ReturnId)
                       orderby l.ReturnId, l.Id
                       select new
                       {
                           l.ReturnId,
                           l.SaleLineId,
                           l.ProductId,
                           l.Quantity,
                           l.Amount,
                           l.CostReversed,
                           l.Restocked,
                           SaleId = r == null ? null : r.OriginalSaleId,
                           Status = r == null ? (ReturnStatus?)null : r.Status,
                           Total = r == null ? 0 : r.TotalRefunded,
                           Cost = r == null ? 0 : r.CogsReversed,
                           User = r == null ? null : r.CashierUserId,
                           Register = r == null ? null : r.RegisterId,
                           Shift = r == null ? null : r.ShiftId,
                           Operation = r == null ? null : r.OperationId
                       };
        await foreach (var r in returned.AsAsyncEnumerable())
        {
            Key(r.ReturnId); Key(r.User); Key(r.Register); Key(r.Shift); Key(r.Operation);
            if (r.SaleId != id || r.Status != ReturnStatus.Completed || !lines.TryGetValue(r.SaleLineId, out var line) ||
                r.ProductId != line.ProductId || !Quantity(r.Quantity) || !Money(r.Amount) || !Money(r.CostReversed) ||
                !r.Restocked && r.CostReversed != 0 || !Money(r.Total) || !Money(r.Cost)) Fail();
            if (group != r.ReturnId)
            {
                FlushLines(); group = r.ReturnId; groupAmount = groupCost = 0;
                expectedAmount = r.Total; expectedCost = r.Cost;
            }
            groupAmount += r.Amount; groupCost += r.CostReversed;
            if (!Returned.TryGetValue(r.SaleLineId, out var totals)) Returned[r.SaleLineId] = totals = new();
            totals.Quantity += r.Quantity; totals.Amount += r.Amount; totals.Cost += r.CostReversed;
            if (r.Restocked) totals.Restocked += r.Quantity;
        }
        FlushLines();
        if (await db.Returns.AnyAsync(r => r.OriginalSaleId == id && !db.ReturnLines.Any(l => l.ReturnId == r.Id))) Fail();
        foreach (var (lineId, totals) in Returned)
        {
            var line = lines[lineId];
            if (totals.Quantity > line.Quantity || totals.Restocked > totals.Quantity ||
                totals.Amount != CommercialLedger.Money(line.Total * totals.Quantity / line.Quantity) ||
                totals.Cost != CommercialLedger.Money(line.CostTotal * totals.Restocked / line.Quantity)) Fail();
        }
        if (Sale.Status != (Returned.Count == 0 ? SaleStatus.Completed : SaleStatus.PartiallyRefunded)) Fail();

        group = null; groupAmount = expectedAmount = 0;
        void FlushRefunds() { if (group is not null && groupAmount != expectedAmount) Fail(); }
        var refunds = from f in db.PaymentRefunds.AsNoTracking()
                      join r in db.Returns.AsNoTracking() on f.ReturnId equals r.Id into parents
                      from r in parents.DefaultIfEmpty()
                      where paymentIds.Contains(f.PaymentId) || returnIds.Contains(f.ReturnId)
                      orderby f.ReturnId, f.Id
                      select new
                      {
                          f.ReturnId,
                          f.PaymentId,
                          f.Kind,
                          f.Amount,
                          f.State,
                          f.OperationId,
                          f.CashierUserId,
                          f.RegisterId,
                          f.ShiftId,
                          SaleId = r == null ? null : r.OriginalSaleId,
                          Total = r == null ? 0 : r.TotalRefunded,
                          User = r == null ? null : r.CashierUserId,
                          Register = r == null ? null : r.RegisterId,
                          Shift = r == null ? null : r.ShiftId,
                          Operation = r == null ? null : r.OperationId
                      };
        await foreach (var f in refunds.AsAsyncEnumerable())
        {
            if (f.SaleId != id || !payments.TryGetValue(f.PaymentId, out var payment) || payment.Kind != f.Kind ||
                f.Amount <= 0 || !Money(f.Amount) || f.OperationId != f.Operation || f.CashierUserId != f.User ||
                f.RegisterId != f.Register || f.ShiftId != f.Shift ||
                f.State is not (TenderState.Refunded or TenderState.RequiresReconciliation) ||
                f.Kind is not (TenderKind.Card or TenderKind.Transfer) && f.State != TenderState.Refunded) Fail();
            if (group != f.ReturnId) { FlushRefunds(); group = f.ReturnId; groupAmount = 0; expectedAmount = f.Total; }
            groupAmount += f.Amount;
            Refunded[f.PaymentId] = Refunded.GetValueOrDefault(f.PaymentId) + f.Amount;
        }
        FlushRefunds();
        if (await db.Returns.AnyAsync(r => r.OriginalSaleId == id && r.TotalRefunded != 0 &&
            !db.PaymentRefunds.Any(f => f.ReturnId == r.Id))) Fail();
        if (Refunded.Any(p => p.Value > payments[p.Key].Amount) ||
            Refunded.Values.Sum() != Returned.Values.Sum(t => t.Amount)) Fail();

        await ReadStockHistoryAsync(db, lineIds);
        await ReadRewardsAsync(db);
    }

    private async Task ReadStockHistoryAsync(AppDbContext db, string[] lineIds)
    {
        await BudgetAsync(db, "inventory_movements",
            "\"Reason\" = 'RETURN' AND (\"SaleLineId\" IN (SELECT \"Id\" FROM sale_lines WHERE \"SaleId\" = {0}) OR \"DocumentId\" IN (SELECT id FROM returns WHERE original_sale_id = {0}))",
            ["Id", "SaleLineId", "ProductId", "DocumentId", "OperationId", "CashierUserId", "RegisterId", "ShiftId"],
            long.MaxValue, long.MaxValue, Sale.Id);
        var original = Stock.SelectMany(g => g).ToDictionary(m => (m.SaleLineId!, m.ProductId));
        var restored = new Dictionary<(string, string), decimal>();
        var returnIds = db.Returns.Where(r => r.OriginalSaleId == Sale.Id).Select(r => r.Id);
        var query = from m in db.InventoryMovements.AsNoTracking()
                    join r in db.Returns.AsNoTracking() on m.DocumentId equals r.Id into parents
                    from r in parents.DefaultIfEmpty()
                    where m.Reason == "RETURN" && (lineIds.Contains(m.SaleLineId!) || returnIds.Contains(m.DocumentId))
                    select new
                    {
                        m.SaleLineId,
                        m.ProductId,
                        m.Quantity,
                        m.UnitCost,
                        m.StockBefore,
                        m.StockAfter,
                        m.OperationId,
                        m.CashierUserId,
                        m.RegisterId,
                        m.ShiftId,
                        SaleId = r == null ? null : r.OriginalSaleId,
                        Operation = r == null ? null : r.OperationId,
                        User = r == null ? null : r.CashierUserId,
                        Register = r == null ? null : r.RegisterId,
                        Shift = r == null ? null : r.ShiftId,
                        HasLine = db.ReturnLines.Any(l => l.ReturnId == m.DocumentId && l.SaleLineId == m.SaleLineId && l.Restocked)
                    };
        await foreach (var m in query.AsAsyncEnumerable())
        {
            if (m.SaleLineId is null || !original.TryGetValue((m.SaleLineId, m.ProductId), out var source) ||
                m.SaleId != Sale.Id || !m.HasLine || !Quantity(m.Quantity) || m.UnitCost != source.UnitCost ||
                m.StockBefore < 0 || m.StockAfter != m.StockBefore + m.Quantity ||
                m.OperationId != m.Operation || m.CashierUserId != m.User || m.RegisterId != m.Register || m.ShiftId != m.Shift) Fail();
            var key = (m.SaleLineId!, m.ProductId);
            restored[key] = restored.GetValueOrDefault(key) + m.Quantity;
        }
        foreach (var line in Lines)
        {
            decimal quantity = Returned.GetValueOrDefault(line.Id)?.Restocked ?? 0m;
            foreach (var m in Stock[line.Id])
                if (restored.GetValueOrDefault((line.Id, m.ProductId)) != Math.Round(-m.Quantity * quantity / line.Quantity, 3)) Fail();
        }
    }

    private async Task ReadRewardsAsync(AppDbContext db)
    {
        await BudgetAsync(db, "account_entries", "\"DocumentId\" = {0} AND \"EntryType\" IN ('SALE_REWARD', 'RETURN_REWARD')",
            ["AccountId", "EntryType"], long.MaxValue, long.MaxValue, Sale.Id);
        await foreach (var e in db.AccountEntries.AsNoTracking().Where(e => e.DocumentId == Sale.Id &&
            (e.EntryType == "SALE_REWARD" || e.EntryType == "RETURN_REWARD"))
            .Select(e => new { e.AccountKind, e.AccountId, e.EntryType, e.Amount }).AsAsyncEnumerable())
        {
            if (e.AccountKind != AccountKind.Loyalty || e.AccountId != Sale.CustomerId) Fail();
            if (e.EntryType == "SALE_REWARD") { if (e.Amount <= 0 || !Money(e.Amount)) Fail(); RewardEarned += e.Amount; }
            else { if (e.Amount >= 0 || !Money(-e.Amount)) Fail(); RewardReversed -= e.Amount; }
        }
        decimal expected = Sale.CustomerId is not null && !Payments.Any(p => p.Kind == TenderKind.Loyalty)
            ? CommercialLedger.Money(Items.Sum(i => (decimal)i.PointsRewarded * (decimal)i.Quantity)) : 0m;
        decimal reversed = Math.Min(expected, CommercialLedger.Money(Items.Sum(i =>
            (decimal)i.PointsRewarded * (Returned.GetValueOrDefault(i.SaleLineId!)?.Quantity ?? 0m))));
        if (RewardEarned != expected || RewardReversed != reversed) Fail();
    }
}
