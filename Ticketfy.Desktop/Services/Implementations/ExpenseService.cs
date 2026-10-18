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

public class ExpenseService : IExpenseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ExpenseService(AppDbContext db, IEscPosPrinterService? printerService = null, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ExpenseService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, PermissionCatalog.ExpensesManage);
    public async Task<List<ExpenseDto>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ExpensesManage, false);
        return (await db.Expenses.AsNoTracking().OrderByDescending(e => e.Date).ToListAsync()).Select(Map).ToList();
    }
    public async Task<ExpenseDto> CreateAsync(ExpenseDto dto)
    {
        string operation = CommercialLedger.OperationId(dto.OperationId ?? dto.Id);
        if (dto.Amount <= 0 || CommercialLedger.Money(dto.Amount) != dto.Amount || string.IsNullOrWhiteSpace(dto.Description))
            throw new InvalidOperationException("Indique un importe positivo y el concepto del gasto.");
        bool cash = dto.PaymentMethod is "Efectivo" or "Cash";
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
    private async Task<(decimal Sales, decimal Cogs, decimal Returns, decimal Reversed, decimal Expenses, decimal CashExpenses, decimal Cash, decimal Card)> TotalsAsync(DateTime? start, DateTime? end)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsProfits, false);
        var allSales = await db.Sales.AsNoTracking().ToListAsync();
        var sales = allSales.Where(s => s.Date.IsInDateRange(start, end) && (s.LedgerVersion > 0 || s.IsCancelled == 0)).ToList();
        var ledgerIds = allSales.Where(s => s.LedgerVersion > 0).Select(s => s.Id).ToHashSet();
        var returns = (await db.Returns.AsNoTracking().ToListAsync()).Where(r => ledgerIds.Contains(r.OriginalSaleId) && r.CreatedAt.IsInDateRange(start, end)).ToList();
        var expenses = await db.Expenses.AsNoTracking().ToListAsync();
        decimal ExpenseAmount(ExpenseEntity e) => (e.Date.IsInDateRange(start, end) ? e.Amount : 0m)
            - (e.IsCancelled && e.CancelledAtUtc is not null && e.CancelledAtUtc.IsInDateRange(start, end) ? e.Amount : 0m);
        var payments = (await db.SalePayments.AsNoTracking().ToListAsync()).Where(p => p.CreatedAtUtc.IsInDateRange(start, end)).ToList();
        var refunds = (await db.PaymentRefunds.AsNoTracking().ToListAsync()).Where(r => r.State == TenderState.Refunded && r.CreatedAtUtc.IsInDateRange(start, end)).ToList();
        var settled = await (from s in db.RefundSettlements.AsNoTracking()
                             join r in db.PaymentRefunds.AsNoTracking()
            on s.RefundId equals r.Id
                             select new { s.CreatedAtUtc, s.Amount, r.Kind }).ToListAsync();
        decimal cardSettlements = settled.Where(s => s.Kind == TenderKind.Card && s.CreatedAtUtc.IsInDateRange(start, end)).Sum(s => s.Amount);
        return (sales.Sum(s => s.Total), sales.Sum(s => s.TotalCost), returns.Sum(r => r.TotalRefunded), returns.Sum(r => r.CogsReversed),
            expenses.Sum(ExpenseAmount), expenses.Where(e => e.PaymentMethod is "Efectivo" or "Cash").Sum(ExpenseAmount),
            payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount) - refunds.Where(r => r.Kind == TenderKind.Cash).Sum(r => r.Amount)
                + sales.Where(s => s.LedgerVersion == 0).Sum(s => s.CashAmount),
            payments.Where(p => p.Kind == TenderKind.Card).Sum(p => p.Amount) - refunds.Where(r => r.Kind == TenderKind.Card).Sum(r => r.Amount)
                - cardSettlements + sales.Where(s => s.LedgerVersion == 0).Sum(s => s.CardAmount));
    }
    public async Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var t = await TotalsAsync(startDate, endDate);
        decimal revenue = t.Sales - t.Returns, cogs = t.Cogs - t.Reversed, gross = revenue - cogs;
        return new(revenue, cogs, gross, t.Expenses, gross - t.Expenses, t.Cash, t.CashExpenses, t.Card);
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
        e.PaymentMethod, e.RegisteredByUser, e.OperationId, e.Reference, e.IsCancelled);
}
