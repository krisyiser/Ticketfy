using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Core.Models;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public partial class ExpenseService : IExpenseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ExpenseService(AppDbContext db, IEscPosPrinterService? printerService = null, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ExpenseService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, PermissionCatalog.ExpensesManage);
    public async Task<List<ExpenseDto>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ExpensesManage, false);
        var period = new BusinessDateQuery(startDate ?? DateTime.MinValue, endDate ?? DateTime.MaxValue);
        var rows = await ReadRowsAsync(db, period, shiftId, 0, LegacyCatalogReadPolicy.Limit, startDate is null && endDate is null);
        LegacyCatalogReadPolicy.RequireComplete(rows.HasMore);
        return rows.Items;
    }
    public async Task<ExpenseDto> CreateAsync(ExpenseDto dto)
    {
        string operation = CommercialLedger.OperationId(dto.OperationId ?? dto.Id);
        if (dto.Amount <= 0 || CommercialLedger.Money(dto.Amount) != dto.Amount || string.IsNullOrWhiteSpace(dto.Description))
            throw new InvalidOperationException("Indique un importe positivo y el concepto del gasto.");
        var kind = TenderMethodCatalog.Parse(dto.PaymentMethod);
        bool cash = kind == TenderKind.Cash;
        dto = dto with { PaymentMethod = TenderMethodCatalog.Label(kind) };
        if (!cash && string.IsNullOrWhiteSpace(dto.Reference)) throw new InvalidOperationException("Indique la referencia del pago del gasto.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operation, "EXPENSE", dto.Amount, dto.Category, dto.Description, dto.PaymentMethod, dto.Reference);
        var existing = await db.Expenses.SingleOrDefaultAsync(e => e.OperationId == operation);
        if (existing is not null)
        {
            if (existing.Amount != dto.Amount || existing.Description != dto.Description) throw new InvalidOperationException("Clave de gasto reutilizada.");
            return Map(existing);
        }
        var entity = new ExpenseEntity
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
            OperationId = operation,
            Category = dto.Category,
            Amount = dto.Amount,
            Description = dto.Description,
            Date = DateTimeOffset.UtcNow.ToString("O"),
            PaymentMethod = dto.PaymentMethod,
            Reference = dto.Reference,
            RegisteredByUser = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId
        };
        db.Expenses.Add(entity);
        if (cash) await CommercialLedger.CashAsync(db, actor, operation, entity.Id, dto.Amount, true, MovementType.GastoOperativo, dto.Description);
        CommercialLedger.Journal(db, actor, operation, "EXPENSE_POSTED", entity.Id, entity.Amount, entity.Description);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return Map(entity);
    }
    public Task<bool> DeleteAsync(string id) => throw new InvalidOperationException("Los gastos no se eliminan. Cancele con motivo y confirme la restitución del pago.");
    public async Task<bool> CancelAsync(string id, string reason, string operationId, bool reversalVerified = false)
    {
        CommercialLedger.OperationId(operationId);
        if (!reversalVerified) throw new InvalidOperationException("Confirme que la salida fue revertida: efectivo devuelto a caja o abono bancario comprobado.");
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Indique el motivo de cancelación.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operationId, "EXPENSE_VOID", id, reason);
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == id);
        if (expense is null) return false;
        if (expense.IsCancelled) return true;
        expense.IsCancelled = true; expense.CancelledAtUtc = DateTimeOffset.UtcNow.ToString("O"); expense.CancellationReason = reason;
        if (expense.PaymentMethod is "Efectivo" or "Cash")
            await CommercialLedger.CashAsync(db, actor, operationId, id, expense.Amount, false, MovementType.CancelacionGasto, reason);
        CommercialLedger.Journal(db, actor, operationId, "EXPENSE_CANCELLED", id, expense.Amount, reason);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return true;
    }
    private sealed record FinancialTotals(decimal Sales, decimal Cogs, decimal Returns, decimal Reversed, decimal Expenses,
        decimal CashExpenses, decimal Cash, decimal Card, decimal Transfer)
    {
        public FinancialSummaryDto Summary()
        {
            decimal revenue = Sales - Returns, cogs = Cogs - Reversed, gross = revenue - cogs;
            return new(revenue, cogs, gross, Expenses, gross - Expenses, Cash, CashExpenses, Card, Transfer);
        }
    }
    private async Task<FinancialTotals> TotalsAsync(DateTime? start, DateTime? end, string? shiftId = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsProfits, false);
        return await TotalsAsync(db, new BusinessDateQuery(start ?? DateTime.MinValue, end ?? DateTime.MaxValue), shiftId);
    }
    private static async Task<FinancialTotals> TotalsAsync(AppDbContext db, BusinessDateQuery period, string? shiftId)
    {
        var start = period.Start; var end = period.End;
        bool InPeriod(string? date) => BusinessTime.TryLocal(date, out var local, period.Zone) && local >= start && local <= end;
        // SQLite money is stored as scaled integers. Aggregate streamed decimal scalars
        // inside this single read snapshot; do not hydrate items JSON, people or whole ledgers.
        decimal sales = 0m, cogs = 0m, returned = 0m, reversedCogs = 0m, expenses = 0m, cashExpenses = 0m;
        decimal cash = 0m, card = 0m, transfer = 0m;
        void Receipt(TenderKind? kind, decimal amount)
        {
            switch (kind)
            {
                case TenderKind.Cash: cash += amount; break;
                case TenderKind.Card: card += amount; break;
                case TenderKind.Transfer: transfer += amount; break;
            }
        }
        static bool IsCash(string method) => method is "Efectivo" or "Cash";
        await foreach (var sale in db.Sales.AsNoTracking().InCandidateDateRange(s => s.Date, start, end)
            .Where(s => (s.LedgerVersion > 0 || s.IsCancelled == 0) && (shiftId == null || s.ShiftId == shiftId))
            .Select(s => new { s.Date, s.Total, s.TotalCost, s.LedgerVersion, s.CashAmount, s.CardAmount }).AsAsyncEnumerable())
        {
            if (!InPeriod(sale.Date)) continue;
            sales += sale.Total; cogs += sale.TotalCost;
            if (sale.LedgerVersion == 0) { cash += sale.CashAmount; card += sale.CardAmount; }
        }
        var returnQuery = from r in db.Returns.AsNoTracking()
                          join s in db.Sales.AsNoTracking() on r.OriginalSaleId equals s.Id
                          where s.LedgerVersion > 0 && (shiftId == null || r.ShiftId == shiftId)
                          select new { r.CreatedAt, r.TotalRefunded, r.CogsReversed };
        await foreach (var row in returnQuery.InCandidateDateRange(r => r.CreatedAt, start, end).AsAsyncEnumerable())
        {
            if (!InPeriod(row.CreatedAt)) continue;
            returned += row.TotalRefunded; reversedCogs += row.CogsReversed;
        }
        await foreach (var expense in db.Expenses.AsNoTracking().Where(e => shiftId == null || e.ShiftId == shiftId)
            .InCandidateDateRange(e => e.Date, start, end).Select(e => new { e.Date, e.Amount, e.PaymentMethod }).AsAsyncEnumerable())
        {
            if (!InPeriod(expense.Date)) continue;
            expenses += expense.Amount;
            if (IsCash(expense.PaymentMethod)) cashExpenses += expense.Amount;
        }
        // Reversals belong to the shift that received the money, not the original expense's shift.
        await foreach (var reversal in ReversalScalars(db, shiftId).InCandidateDateRange(e => e.Date, start, end).AsAsyncEnumerable())
        {
            if (!InPeriod(reversal.Date)) continue;
            expenses -= reversal.Amount;
            if (IsCash(reversal.PaymentMethod)) cashExpenses -= reversal.Amount;
        }
        await foreach (var payment in db.SalePayments.AsNoTracking().Where(p => p.State == TenderState.Captured && (shiftId == null || p.ShiftId == shiftId))
            .InCandidateDateRange(p => p.CreatedAtUtc, start, end).Select(p => new { p.CreatedAtUtc, p.Kind, p.Amount }).AsAsyncEnumerable())
            if (InPeriod(payment.CreatedAtUtc)) Receipt(payment.Kind, payment.Amount);
        await foreach (var refund in db.PaymentRefunds.AsNoTracking().Where(r => r.State == TenderState.Refunded && (shiftId == null || r.ShiftId == shiftId))
            .InCandidateDateRange(r => r.CreatedAtUtc, start, end).Select(r => new { r.CreatedAtUtc, r.Kind, r.Amount }).AsAsyncEnumerable())
            if (InPeriod(refund.CreatedAtUtc)) Receipt(refund.Kind, -refund.Amount);
        var settlements = from s in db.RefundSettlements.AsNoTracking()
                          join r in db.PaymentRefunds.AsNoTracking() on s.RefundId equals r.Id
                          where shiftId == null || s.ShiftId == shiftId
                          select new { s.CreatedAtUtc, s.Amount, r.Kind };
        await foreach (var settlement in settlements.InCandidateDateRange(s => s.CreatedAtUtc, start, end).AsAsyncEnumerable())
            if (InPeriod(settlement.CreatedAtUtc)) Receipt(settlement.Kind, -settlement.Amount);
        // Receipts are not accrual revenue. Debt collections and credit refunds must not change sales or COGS again.
        await foreach (var collection in db.CustomerPayments.AsNoTracking().Where(p => p.Status == PaymentStatus.Completed && (shiftId == null || p.ShiftId == shiftId))
            .InCandidateDateRange(p => p.Date, start, end).Select(p => new { p.Date, p.Method, p.Amount }).AsAsyncEnumerable())
        {
            if (!InPeriod(collection.Date)) continue;
            TenderKind? kind = collection.Method switch
            {
                "Efectivo" or "Cash" => TenderKind.Cash,
                "Tarjeta" or "Card" => TenderKind.Card,
                "Transferencia" or "Transfer" => TenderKind.Transfer,
                _ => null
            };
            Receipt(kind, collection.Amount);
        }
        await foreach (var refund in db.AccountCreditSettlements.AsNoTracking().Where(s => s.AccountKind == AccountKind.Customer
            && s.Kind == AccountCreditSettlementKind.Refund && (shiftId == null || s.ShiftId == shiftId))
            .InCandidateDateRange(s => s.CreatedAtUtc, start, end).Select(s => new { s.CreatedAtUtc, s.TenderKind, s.Amount }).AsAsyncEnumerable())
            if (InPeriod(refund.CreatedAtUtc)) Receipt(refund.TenderKind, -refund.Amount);
        return new(sales, cogs, returned, reversedCogs, expenses, cashExpenses, cash, card, transfer);
    }
    public async Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
    {
        return (await TotalsAsync(startDate, endDate, shiftId)).Summary();
    }
    public async Task<NetProfitReportModel> CalculateTrueNetProfitAsync(DateTime startDate, DateTime endDate)
    {
        var t = await TotalsAsync(startDate, endDate);
        decimal net = t.Sales - t.Returns, cogs = t.Cogs - t.Reversed, gross = net - cogs, profit = gross - t.Expenses;
        return new NetProfitReportModel
        {
            StartDate = startDate,
            EndDate = endDate,
            GrossSales = t.Sales,
            TotalRefunds = t.Returns,
            NetSales = net,
            CostOfGoodsSold = cogs,
            GrossProfit = gross,
            GrossMarginPercentage = net == 0 ? 0 : CommercialLedger.Money(gross / net * 100),
            OperatingExpenses = t.Expenses,
            NetProfit = profit,
            NetProfitPercentage = net == 0 ? 0 : CommercialLedger.Money(profit / net * 100)
        };
    }
    private static ExpenseDto Map(ExpenseEntity e) => new(e.Id, e.Category, e.Amount, e.Date, e.Description,
        e.PaymentMethod, e.RegisteredByUser, e.OperationId, e.Reference, e.IsCancelled,
        e.CancelledAtUtc, e.CancellationReason, e.ShiftId, e.RegisterId);
}
