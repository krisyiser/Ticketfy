using Avalonia.Headless.XUnit;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task ExpensePages_LargeHistoryReadsOnlyVisibleDetailsAndKeepsExactDecimalTotals()
    {
        await using var db = Db();
        string date = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH RECURSIVE rows(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM rows WHERE i < 10000)
            INSERT INTO Expenses (Id, Category, Amount, Date, Description, PaymentMethod, RegisteredByUser, IsCancelled, ShiftId, RegisterId)
            SELECT printf('VOLUME-%05d', i), 'Prueba', 101, {date},
                   'Synthetic expense detail that is not needed for whole-history summaries', 'Tarjeta', 'synthetic',
                   0, 'SHIFT-1', 'TEST-REGISTER'
            FROM rows;
            """);
        var observer = new ExpenseDetailBudget();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new ExpenseService(new TestDbContextFactory(options), _actor);
        var first = await service.GetPageAsync(0, activeShiftOnly: true);
        var last = await service.GetPageAsync(100, activeShiftOnly: true);
        Assert.Equal(10001, first.TotalCount); Assert.Equal(100, first.Items.Count);
        Assert.Equal("VOLUME-00000", first.Items[0].Id);
        Assert.Equal("VOLUME-10000", Assert.Single(last.Items).Id);
        Assert.Equal(10101.01m, first.Summary!.TotalExpenses);
        Assert.Equal(first.Summary, last.Summary); Assert.False(last.HasMore);
        Assert.Equal(2, observer.DetailQueries); Assert.True(observer.ScalarQueries >= 4);
    }

    private sealed class ExpenseDetailBudget : DbCommandInterceptor
    {
        public int DetailQueries { get; private set; }
        public int ScalarQueries { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM Expenses", StringComparison.Ordinal))
            { DetailQueries++; Assert.Contains("LIMIT", command.CommandText); }
            else if (command.CommandText.Contains("FROM \"Expenses\"", StringComparison.Ordinal))
            {
                ScalarQueries++;
                Assert.DoesNotContain("\"Description\"", command.CommandText);
                Assert.DoesNotContain("\"Reference\"", command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }

    [AvaloniaFact]
    public async Task ExpenseWorkspace_BoundsVisibleRowsWithoutReducingPeriodTotals()
    {
        await SeedWorkspaceExpensesAsync(251);
        var factory = new TestDbContextFactory(_options);
        var model = new ExpensesViewModel(new ExpenseService(factory, _actor));
        await model.LoadExpensesAsync();
        Assert.Equal(100, model.Expenses.Count);
        Assert.Equal(253.51m, model.TotalExpenses);
        Assert.Equal(251, model.TotalCount);
        var ids = model.Expenses.Select(e => e.Id).ToList();
        var view = new ExpensesView();
        var window = Show(view, model);
        try
        {
            await model.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(100, model.Expenses.Count); Assert.True(model.CanLoadPreviousPage);
            ids.AddRange(model.Expenses.Select(e => e.Id));
            await model.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(51, model.Expenses.Count); Assert.False(model.CanLoadNextPage);
            ids.AddRange(model.Expenses.Select(e => e.Id));
            Assert.Equal(251, ids.Distinct().Count());
            Assert.Equal(253.51m, model.TotalExpenses);
            await model.PreviousPageCommand.ExecuteAsync(null);
            Assert.Contains("Página 2", model.PageStatus);
            await model.RefreshExpensesCommand.ExecuteAsync(null);
            Assert.Contains("Página 1", model.PageStatus);
            Assert.True(model.FinancialVisible);
        }
        finally { window.Close(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ExpenseService(factory, _actor).GetAllAsync());
    }

    [AvaloniaFact]
    public async Task ExpenseWorkspace_CommittedExpenseSurvivesRefreshFailureWithoutFalseFailureOrDuplicate()
    {
        await Sales.SaveAsync(Sale("WORKSPACE-FLOAT"));
        var factory = new TestDbContextFactory(_options);
        var service = new WorkspaceExpenses(new ExpenseService(factory, _actor)) { FailAfterCreate = true };
        var model = new ExpensesViewModel(service);
        await model.LoadExpensesAsync();
        model.ExpenseAmount = 5m; model.ExpenseDescription = "Synthetic verified payment";
        await model.AddExpenseCommand.ExecuteAsync(null);
        Assert.Equal(0m, model.ExpenseAmount);
        Assert.False(model.IsFeedbackError);
        Assert.Contains("registrado", model.FeedbackMessage);
        Assert.Contains("consulta", model.FeedbackMessage);
        Assert.Empty(model.Expenses);
        await model.AddExpenseCommand.ExecuteAsync(null);
        await using var db = Db();
        Assert.Single(await db.Expenses.ToListAsync());
        Assert.Equal(15m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Fact]
    public async Task ExpensePages_OrderHistoricalDatesKeepUnknownRecordsAndValidateBoundaries()
    {
        await using var db = Db();
        ExpenseEntity Expense(string id, string date, decimal amount, string shift = "SHIFT-1") => new()
        { Id = id, Date = date, Amount = amount, ShiftId = shift, Description = id, PaymentMethod = "Tarjeta" };
        db.Expenses.AddRange(
            Expense("OLD", "30/11/2026 23:59:59", 99m),
            Expense("LOCAL", "01/12/2026 08:00:00", 1.11m),
            Expense("A", BusinessTime.ToUtc(new(2026, 12, 1, 10, 0, 0)).ToString("O"), 3.33m),
            Expense("B", "2026-12-01 10:00:00", 4.44m),
            Expense("LATEST", "02/12/2026 12:00:00", 2.22m),
            Expense("UNKNOWN", "unparseable", 500m),
            Expense("OTHER", "02/12/2026 13:00:00", 1000m, "OTHER"));
        await db.SaveChangesAsync();
        var service = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var start = new DateTime(2026, 12, 1); var end = new DateTime(2026, 12, 2);
        var first = await service.GetPageAsync(0, 2, start, end, "SHIFT-1");
        var second = await service.GetPageAsync(1, 2, start, end, "SHIFT-1");
        Assert.Equal(new[] { "LATEST", "A" }, first.Items.Select(e => e.Id));
        Assert.Equal(new[] { "B", "LOCAL" }, second.Items.Select(e => e.Id));
        Assert.Equal("01/12/2026 08:00", second.Items[1].DateDisplay);
        Assert.Equal("", second.Items[1].CancellationDateDisplay);
        Assert.Equal(4, first.TotalCount); Assert.True(first.HasMore); Assert.False(second.HasMore);
        Assert.Equal(1, first.UnparseableDateCount);
        Assert.Equal(11.10m, first.Summary!.TotalExpenses); Assert.Equal(first.Summary, second.Summary);
        var all = await service.GetPageAsync(0, shiftId: "SHIFT-1");
        Assert.Equal("UNKNOWN", all.Items.Last().Id); Assert.Equal(6, all.TotalCount);
        Assert.Equal("Fecha sin verificar", all.Items.Last().DateDisplay);
        Assert.Equal(110.10m, all.Summary!.TotalExpenses);
        Assert.Empty((await service.GetPageAsync(99, 100, start, end)).Items);
        Assert.Empty((await service.GetPageAsync(0, startDate: DateTime.MinValue, endDate: DateTime.MinValue)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(0, 201));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetPageAsync(int.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPageAsync(0, startDate: end, endDate: start));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPageAsync(0, shiftId: "SHIFT-1", activeShiftOnly: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPageAsync(0, startDate: start, activeShiftOnly: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ExpenseService(new TestDbContextFactory(_options)).GetPageAsync(0));
    }

    [AvaloniaFact]
    public async Task ExpenseWorkspace_ExpensePermissionDoesNotExposeProfitOrAnotherCashiersBalance()
    {
        await SeedWorkspaceExpensesAsync(1);
        await using var db = Db();
        var user = await db.Users.SingleAsync(); user.RoleString = "CAJERO";
        db.Settings.Add(new SettingEntity { Key = "RolePermissions_CAJERO", Value = "{\"expenses.manage\":true}" });
        await db.SaveChangesAsync();
        var factory = new TestDbContextFactory(_options);
        var permissions = new UserRolePermissionEngine(new SettingsService(factory));
        var session = new SessionManager(); session.StartSession(permissions.MapToModel(user));
        var service = new ExpenseService(factory, new CommercialOperationContext(session, permissions, "TEST-REGISTER"));
        var page = await service.GetPageAsync(0, activeShiftOnly: true);
        Assert.Single(page.Items); Assert.Null(page.Summary); Assert.True(page.CashVisible);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetFinancialSummaryAsync());
        var model = new ExpensesViewModel(service); await model.InitializationTask;
        Assert.Single(model.Expenses); Assert.False(model.FinancialVisible); Assert.Contains("restringido", model.ReadMessage);
        (await db.Shifts.SingleAsync()).CashierUserId = Guid.NewGuid().ToString();
        await db.SaveChangesAsync();
        var other = await service.GetPageAsync(0);
        Assert.False(other.CashVisible); Assert.Null(other.ActiveShift!.OpeningBalance); Assert.Null(other.ActiveShift.ExpectedBalance);
        (await db.Settings.SingleAsync()).Value = "{\"expenses.manage\":true,\"pos.checkout\":false,\"reports.profits\":true}";
        await db.SaveChangesAsync();
        var financeOnly = await service.GetPageAsync(0);
        Assert.NotNull(financeOnly.Summary); Assert.False(financeOnly.CashVisible);
        user.IsActive = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetPageAsync(0));
        await model.LoadExpensesAsync();
        Assert.Empty(model.Expenses); Assert.False(model.FinancialVisible); Assert.False(model.HasLoadedData);
    }

    [AvaloniaFact]
    public async Task ExpenseWorkspace_FilterChangeAndLogoutClearDataBeforeLateResponse()
    {
        await SeedWorkspaceExpensesAsync(1);
        var inner = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var service = new WorkspaceExpenses(inner);
        var model = new ExpensesViewModel(service); await model.InitializationTask;
        Assert.Single(model.Expenses);
        var prior = await inner.GetPageAsync(0);
        var pending = new TaskCompletionSource<ExpensePageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PageRead = () => pending.Task;
        model.SelectedPeriod = "Hoy";
        Assert.Empty(model.Expenses); Assert.False(model.FinancialVisible); Assert.True(model.IsLoading);
        var read = model.LoadExpensesAsync();
        model.ExpenseAmount = 4m; model.ExpenseDescription = "discarded on logout";
        model.ClearSessionData();
        pending.SetResult(prior);
        await read;
        Assert.Empty(model.Expenses); Assert.False(model.HasLoadedData);
        Assert.False(model.IsLoading); Assert.Equal(0m, model.TotalExpenses); Assert.Equal(0m, model.ExpenseAmount);
        service.PageRead = null;
        model.SelectedPeriod = "Invalid";
        await model.LoadExpensesAsync();
        Assert.False(model.HasLoadedData);
    }

    [Fact]
    public async Task ExpensePage_UnattributedShiftDoesNotExposeCashWithoutHistoryPermission()
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync(); user.RoleString = "CAJERO";
        var shift = await db.Shifts.SingleAsync(); shift.CashierUserId = null;
        shift.OpeningBalance = shift.ExpectedBalance = 123m;
        db.Settings.Add(new SettingEntity { Key = "RolePermissions_CAJERO", Value = "{\"expenses.manage\":true}" });
        await db.SaveChangesAsync();
        var factory = new TestDbContextFactory(_options);
        var permissions = new UserRolePermissionEngine(new SettingsService(factory));
        var session = new SessionManager(); session.StartSession(permissions.MapToModel(user));
        var service = new ExpenseService(factory, new CommercialOperationContext(session, permissions, "TEST-REGISTER"));
        var page = await service.GetPageAsync(0, activeShiftOnly: true);
        Assert.False(page.CashVisible);
        Assert.NotNull(page.ActiveShift);
        Assert.Null(page.ActiveShift.OpeningBalance);
        Assert.Null(page.ActiveShift.ExpectedBalance);
    }

    [AvaloniaFact]
    public async Task ExpenseWorkspace_CancelSuccessAndMissingDocumentHaveDistinctOutcomes()
    {
        await Sales.SaveAsync(Sale("CANCEL-FLOAT"));
        var inner = new ExpenseService(new TestDbContextFactory(_options), _actor);
        await inner.CreateAsync(new("CANCEL-UI", "General", 5m, "", "Synthetic", "Efectivo", ""));
        var service = new WorkspaceExpenses(inner) { FailAfterCancel = true };
        var model = new ExpensesViewModel(service) { CancellationReason = "Restituido", ReversalVerified = true };
        await model.InitializationTask;
        await model.DeleteExpenseCommand.ExecuteAsync(Assert.Single(model.Expenses));
        Assert.False(model.IsFeedbackError); Assert.Contains("cancelado", model.FeedbackMessage);
        Assert.Contains("consulta", model.FeedbackMessage); Assert.Empty(model.Expenses);
        await using var db = Db();
        Assert.True((await db.Expenses.SingleAsync()).IsCancelled);
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        service.FailAfterCancel = service.FailReads = false;
        await inner.CreateAsync(new("MISSING", "General", 1m, "", "Synthetic missing row", "Tarjeta", "", Reference: "MISSING-REF"));
        await model.LoadExpensesAsync();
        var missing = model.Expenses.Single(e => e.Id == "MISSING");
        await db.Expenses.Where(e => e.Id == "MISSING").ExecuteDeleteAsync(); // Simulate loss after reading this disposable database.
        model.CancellationReason = "Restituido"; model.ReversalVerified = true;
        await model.DeleteExpenseCommand.ExecuteAsync(missing);
        Assert.True(model.IsFeedbackError); Assert.Contains("no está disponible", model.FeedbackMessage);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "EXPENSE_CANCELLED").ToListAsync());
    }

    private async Task SeedWorkspaceExpensesAsync(int count)
    {
        await using var db = Db();
        for (int i = 0; i < count; i++)
            db.Expenses.Add(new ExpenseEntity
            {
                Id = $"EXP-{i:D4}",
                OperationId = $"EXP-{i:D4}",
                Date = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O"),
                Amount = 1.01m,
                Description = "Synthetic expense",
                PaymentMethod = "Tarjeta",
                Reference = $"BANK-{i:D4}",
                ShiftId = "SHIFT-1",
                RegisterId = "TEST-REGISTER",
                RegisteredByUser = CommercialTestSession.UserId.ToString()
            });
        await db.SaveChangesAsync();
    }

    private sealed class WorkspaceExpenses(IExpenseService inner) : IExpenseService
    {
        public bool FailReads { get; set; }
        public bool FailAfterCreate { get; set; }
        public bool FailAfterCancel { get; set; }
        public Func<Task<ExpensePageDto>>? PageRead { get; set; }
        public async Task<ExpenseDto> CreateAsync(ExpenseDto dto)
        {
            var result = await inner.CreateAsync(dto);
            if (FailAfterCreate) FailReads = true;
            return result;
        }
        public Task<List<ExpenseDto>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
            => FailReads ? Task.FromException<List<ExpenseDto>>(new IOException("Synthetic read failure")) : inner.GetAllAsync(startDate, endDate, shiftId);
        public Task<ExpensePageDto> GetPageAsync(int page, int size = 100, DateTime? startDate = null, DateTime? endDate = null,
            string? shiftId = null, bool activeShiftOnly = false, bool cancellationsOnly = false)
            => PageRead?.Invoke() ?? (FailReads ? Task.FromException<ExpensePageDto>(new IOException("Synthetic read failure"))
                : inner.GetPageAsync(page, size, startDate, endDate, shiftId, activeShiftOnly, cancellationsOnly));
        public Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
            => inner.GetFinancialSummaryAsync(startDate, endDate, shiftId);
        public Task<NetProfitReportModel> CalculateTrueNetProfitAsync(DateTime startDate, DateTime endDate)
            => inner.CalculateTrueNetProfitAsync(startDate, endDate);
        public Task<bool> DeleteAsync(string id) => inner.DeleteAsync(id);
        public async Task<bool> CancelAsync(string id, string reason, string operationId, bool reversalVerified = false)
        {
            bool result = await inner.CancelAsync(id, reason, operationId, reversalVerified);
            if (FailAfterCancel) FailReads = true;
            return result;
        }
    }
}
