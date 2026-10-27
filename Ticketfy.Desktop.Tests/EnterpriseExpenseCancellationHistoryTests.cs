using System.Data.Common;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("Efectivo")]
    [InlineData("Tarjeta")]
    [InlineData("Transferencia")]
    public async Task ExpenseCancellationHistory_ExplainsReceiptFromOtherPeriodRegisterShiftAndOperator(string method)
    {
        await using var db = Db();
        db.Expenses.Add(OldExpense("OLD", method));
        await db.SaveChangesAsync();
        var service = new ExpenseService(new TestDbContextFactory(_options), _actor);
        Assert.True(await service.CancelAsync("OLD", "Restitución comprobada", "RECEIPT-ONCE", true));
        Assert.True(await service.CancelAsync("OLD", "Restitución comprobada", "RECEIPT-ONCE", true));
        var documents = await service.GetPageAsync(0, startDate: BusinessTime.Today, endDate: BusinessTime.Now);
        Assert.Empty(documents.Items);
        Assert.Equal(-12.34m, documents.Summary!.TotalExpenses);
        var page = await service.GetPageAsync(0, cancellationsOnly: true, activeShiftOnly: true);
        var receipt = Assert.Single(page.Cancellations);
        Assert.Empty(page.Items); Assert.Equal(1, page.TotalCount); Assert.False(page.HasMore);
        Assert.False(receipt.IsLegacy);
        Assert.Equal("RECEIPT-ONCE", receipt.OperationId);
        Assert.Equal(CommercialTestSession.UserId.ToString(), receipt.UserId);
        Assert.Equal("TEST-REGISTER", receipt.RegisterId); Assert.Equal("SHIFT-1", receipt.ShiftId);
        Assert.Equal("Restitución comprobada", receipt.Reason);
        Assert.Equal(12.34m, receipt.Amount); Assert.Equal(12.34m, page.CancellationAmount);
        Assert.Equal("OLD-USER", receipt.Original.RegisteredByUser);
        Assert.Equal("OLD-REGISTER", receipt.Original.RegisterId); Assert.Equal("OLD-SHIFT", receipt.Original.ShiftId);
        Assert.Equal("2020-01-01 10:00:00", receipt.Original.Date);
        Assert.Equal(method, receipt.Original.PaymentMethod); Assert.Equal("ORIGINAL-REFERENCE", receipt.Original.Reference);
        Assert.Equal(12.34m, receipt.Original.Amount);
        Assert.Equal(-12.34m, page.Summary!.TotalExpenses);
        Assert.Equal(method == "Efectivo" ? -12.34m : 0m, page.Summary.CashExpenses);
        Assert.Equal(method == "Efectivo" ? 12.34m : 0m, page.ActiveShift!.ExpectedBalance);
        Assert.Contains("no equivale", receipt.EvidenceDisplay, StringComparison.Ordinal);
        var old = await service.GetPageAsync(0, shiftId: "OLD-SHIFT", cancellationsOnly: true);
        Assert.Empty(old.Cancellations); Assert.Equal(12.34m, old.Summary!.TotalExpenses);
    }

    private static ExpenseEntity OldExpense(string id, string method = "Tarjeta") => new()
    {
        Id = id,
        Date = "2020-01-01 10:00:00",
        Amount = 12.34m,
        Description = "Documento histórico sintético",
        PaymentMethod = method,
        Reference = "ORIGINAL-REFERENCE",
        ShiftId = "OLD-SHIFT",
        RegisterId = "OLD-REGISTER",
        RegisteredByUser = "OLD-USER"
    };

    [Fact]
    public async Task ExpenseCancellationHistory_LegacyAndUnknownDatesRemainVisibleWithoutInventingAttributionOrDoubleCounting()
    {
        await using var db = Db();
        foreach (var (id, storedDate) in new[] { ("LEGACY", "01/12/2026 08:00:00"), ("MISSING", (string?)null), ("BAD", "not-a-date"), ("LOGGED", "02/12/2026 12:00:00") })
        {
            var expense = OldExpense(id); expense.IsCancelled = true; expense.CancelledAtUtc = storedDate;
            expense.CancellationReason = "Historical reason";
            db.Expenses.Add(expense);
        }
        db.TransactionJournal.Add(new TransactionJournalEntryEntity
        {
            Id = "JOURNAL",
            EntityId = "LOGGED",
            OperationId = "LOGGED-VOID",
            EventType = "EXPENSE_CANCELLED",
            CreatedAtUtc = "01/12/2026 10:00:00",
            Amount = 12.34m,
            Details = "Journal reason",
            CashierUserId = "",
            ShiftId = "SHIFT-1",
            RegisterId = "TEST-REGISTER"
        });
        await db.SaveChangesAsync();
        var service = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var date = new DateTime(2026, 12, 1);
        var filtered = await service.GetPageAsync(0, 1, date, date, cancellationsOnly: true);
        Assert.Equal(2, filtered.TotalCount); Assert.Equal(2, filtered.UnparseableDateCount);
        Assert.Equal(24.68m, filtered.CancellationAmount); Assert.Equal(-24.68m, filtered.Summary!.TotalExpenses);
        var logged = Assert.Single(filtered.Cancellations);
        Assert.Equal("LOGGED", logged.Original.Id); Assert.Equal("Journal reason", logged.Reason);
        Assert.Equal("01/12/2026 10:00", logged.DateDisplay); Assert.Equal("Sin atribución verificable", logged.UserDisplay);
        var second = await service.GetPageAsync(1, 1, date, date, cancellationsOnly: true);
        var legacy = Assert.Single(second.Cancellations);
        Assert.True(legacy.IsLegacy); Assert.Null(legacy.OperationId); Assert.Null(legacy.UserId); Assert.Null(legacy.ShiftId); Assert.Null(legacy.RegisterId);
        Assert.Equal("Sin atribución verificable", legacy.RegisterDisplay); Assert.Equal("Sin atribución verificable", legacy.ShiftDisplay);
        Assert.Equal(filtered.Summary, second.Summary); Assert.False(second.HasMore);
        Assert.Contains("sin diario", legacy.EvidenceDisplay, StringComparison.Ordinal);
        var all = await service.GetPageAsync(0, cancellationsOnly: true);
        Assert.Equal(new[] { "LOGGED", "LEGACY", "BAD", "MISSING" }, all.Cancellations.Select(c => c.Original.Id));
        Assert.Equal(4, all.TotalCount); Assert.Equal(24.68m, all.CancellationAmount);
        Assert.All(all.Cancellations.TakeLast(2), c => Assert.Equal("Fecha sin verificar", c.DateDisplay));
        var shift = await service.GetPageAsync(0, cancellationsOnly: true, activeShiftOnly: true);
        Assert.Single(shift.Cancellations); Assert.Equal(0, shift.UnparseableDateCount); Assert.Equal(12.34m, shift.CancellationAmount);
        Assert.Empty((await service.GetPageAsync(99, cancellationsOnly: true)).Cancellations);
    }

    [Fact]
    public async Task ExpenseCancellationHistory_LargeHistoryBoundsOriginalAndJournalDetailsAndPreservesTotals()
    {
        await using var db = Db();
        for (int i = 0; i < 251; i++)
        {
            var expense = OldExpense($"E-{i:D3}"); expense.IsCancelled = true; expense.CancelledAtUtc = "2026-12-01 10:00:00";
            db.Expenses.Add(expense);
            if (i % 2 == 0) db.TransactionJournal.Add(new TransactionJournalEntryEntity
            {
                Id = $"J-{i:D3}",
                EntityId = expense.Id,
                EventType = "EXPENSE_CANCELLED",
                OperationId = $"VOID-{i:D3}",
                CreatedAtUtc = expense.CancelledAtUtc,
                Amount = expense.Amount,
                ShiftId = "SHIFT-1"
            });
        }
        await db.SaveChangesAsync();
        var observer = new CancellationQueryObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new ExpenseService(new TestDbContextFactory(options), _actor);
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var page = await service.GetPageAsync(i, cancellationsOnly: true);
            Assert.Equal(i == 2 ? 51 : 100, page.Cancellations.Count);
            Assert.Equal(251, page.TotalCount); Assert.Equal(i < 2, page.HasMore);
            Assert.Equal(3097.34m, page.CancellationAmount);
            Assert.Equal(0m, page.Summary!.TotalExpenses);
            ids.AddRange(page.Cancellations.Select(c => c.Original.Id));
        }
        Assert.Equal(251, ids.Distinct().Count());
        Assert.Equal(3, observer.KeyQueries); Assert.Equal(3, observer.ExpenseDetailQueries); Assert.Equal(3, observer.JournalDetailQueries);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(-1, cancellationsOnly: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(0, 201, cancellationsOnly: true));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetPageAsync(int.MaxValue, cancellationsOnly: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPageAsync(0, shiftId: "S", activeShiftOnly: true, cancellationsOnly: true));
    }

    private sealed class CancellationQueryObserver(Func<Task>? concurrentWrite = null) : DbCommandInterceptor
    {
        public int KeyQueries { get; private set; }
        public int ExpenseDetailQueries { get; private set; }
        public int JournalDetailQueries { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT Id, ExpenseId, Legacy", StringComparison.Ordinal))
            {
                KeyQueries++; Assert.Contains("LIMIT", command.CommandText);
                Assert.DoesNotContain("Description", command.CommandText);
                Assert.NotNull(command.Transaction);
                if (concurrentWrite is not null && KeyQueries == 1) await concurrentWrite();
            }
            else if (command.CommandText.Contains("\"Description\"", StringComparison.Ordinal) && command.CommandText.Contains("FROM \"Expenses\"", StringComparison.Ordinal))
            { ExpenseDetailQueries++; AssertBoundedIds(command.CommandText, "e", "Id"); Assert.NotNull(command.Transaction); }
            else if (command.CommandText.Contains("\"details\"", StringComparison.Ordinal) && command.CommandText.Contains("FROM \"transaction_journal\"", StringComparison.Ordinal))
            { JournalDetailQueries++; AssertBoundedIds(command.CommandText, "t", "id"); Assert.NotNull(command.Transaction); }
            return result;
        }
        private static void AssertBoundedIds(string sql, string alias, string id) =>
            Assert.True(sql.Contains($"WHERE \"{alias}\".\"{id}\" IN ", StringComparison.Ordinal)
                || sql.Contains($"WHERE \"{alias}\".\"{id}\" = ", StringComparison.Ordinal)
                || sql.Contains("WHERE 0", StringComparison.Ordinal), sql);
    }

    [AvaloniaFact]
    public async Task ExpenseCancellationHistory_UiShowsLinkedReceiptWithIndependentPermissionsAndClearsStaleResults()
    {
        await using var db = Db();
        db.Expenses.Add(OldExpense("UI-OLD")); await db.SaveChangesAsync();
        var factory = new TestDbContextFactory(_options);
        await new ExpenseService(factory, _actor).CancelAsync("UI-OLD", "Visible receipt reason", "UI-VOID", true);
        var user = await db.Users.SingleAsync(); user.RoleString = "CAJERO";
        db.Settings.Add(new SettingEntity { Key = "RolePermissions_CAJERO", Value = "{\"expenses.manage\":true,\"pos.checkout\":false,\"reports.profits\":false}" });
        await db.SaveChangesAsync();
        var permissions = new UserRolePermissionEngine(new SettingsService(factory));
        var session = new SessionManager(); session.StartSession(permissions.MapToModel(user));
        var inner = new ExpenseService(factory, new CommercialOperationContext(session, permissions, "TEST-REGISTER"));
        var wrapper = new WorkspaceExpenses(inner);
        var model = new ExpensesViewModel(wrapper); await model.InitializationTask;
        var view = new ExpensesView(); var window = Show(view, model);
        try
        {
            var selector = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, model.HistoryOptions));
            selector.SelectedItem = "Cancelaciones recibidas";
            await model.LoadExpensesAsync();
            Assert.True(model.ShowingCancellations); Assert.Empty(model.Expenses);
            Assert.Single(model.Cancellations); Assert.Equal(12.34m, model.CancellationAmount);
            Assert.Contains("1 cancelaciones", model.PageStatus, StringComparison.Ordinal);
            Assert.False(model.FinancialVisible); Assert.Equal("Restringido", model.CashBalanceDisplay);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Motivo: Visible receipt reason");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Gasto original: UI-OLD");
            var pending = new TaskCompletionSource<ExpensePageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = await inner.GetPageAsync(0, cancellationsOnly: true);
            wrapper.PageRead = () => pending.Task;
            model.SelectedHistory = "Documentos de gasto";
            Assert.Empty(model.Cancellations); Assert.Equal(0m, model.CancellationAmount);
            Assert.Contains("no disponibles", model.CancellationAmountDisplay, StringComparison.Ordinal);
            var read = model.LoadExpensesAsync();
            model.ClearSessionData();
            pending.SetResult(before); await read;
            Assert.Empty(model.Cancellations); Assert.False(model.HasLoadedData);
            wrapper.PageRead = null;
            user.IsActive = false; await db.SaveChangesAsync();
            model.SelectedHistory = "Cancelaciones recibidas"; await model.LoadExpensesAsync();
            Assert.Empty(model.Cancellations); Assert.Equal(0m, model.CancellationAmount); Assert.False(model.HasLoadedData);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inner.GetPageAsync(0, cancellationsOnly: true));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExpenseCancellationHistory_UiPagesFullHistoryAndIgnoresCancellationFromDiscardedRows()
    {
        await using var db = Db();
        for (int i = 0; i < 123; i++)
        {
            var expense = OldExpense($"UI-{i:D3}"); expense.IsCancelled = true; expense.CancelledAtUtc = "2026-01-01 12:00:00";
            db.Expenses.Add(expense);
        }
        db.Expenses.Add(OldExpense("NOT-CANCELLED"));
        await db.SaveChangesAsync();
        var service = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var model = new ExpensesViewModel(service) { SelectedPeriod = "Todo el Histórico" };
        await model.LoadExpensesAsync();
        var stale = model.Expenses.Single(e => e.Id == "NOT-CANCELLED");
        model.CancellationReason = "Must not be submitted"; model.ReversalVerified = true;
        await model.RefreshExpensesCommand.ExecuteAsync(null);
        await model.DeleteExpenseCommand.ExecuteAsync(stale);
        Assert.False((await db.Expenses.AsNoTracking().SingleAsync(e => e.Id == stale.Id)).IsCancelled);
        var current = model.Expenses.Single(e => e.Id == stale.Id);
        model.SelectedHistory = "Cancelaciones recibidas"; await model.LoadExpensesAsync();
        var view = new ExpensesView(); var window = Show(view, model);
        try
        {
            await model.DeleteExpenseCommand.ExecuteAsync(current);
            Assert.Equal(100, model.Cancellations.Count); Assert.Equal(123, model.TotalCount);
            Assert.Equal(1517.82m, model.CancellationAmount); Assert.Equal(12.34m, model.TotalExpenses);
            await ClickTwiceAsync(view, model.NextPageCommand);
            Assert.Equal(23, model.Cancellations.Count); Assert.False(model.CanLoadNextPage);
            Assert.Equal(1517.82m, model.CancellationAmount); Assert.Equal(12.34m, model.TotalExpenses);
            await model.PreviousPageCommand.ExecuteAsync(null);
            Assert.Equal(100, model.Cancellations.Count);
            model.ClearSessionData(); await model.DeleteExpenseCommand.ExecuteAsync(current);
            Assert.Empty(model.Cancellations); Assert.Contains("no disponibles", model.CancellationAmountDisplay, StringComparison.Ordinal);
            Assert.False((await db.Expenses.AsNoTracking().SingleAsync(e => e.Id == stale.Id)).IsCancelled);
            Assert.Empty(await db.TransactionJournal.ToListAsync());
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ExpenseCancellationHistory_WalWriterCannotMixReceiptOriginalSummaryOrCash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-cancellation-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            CommercialOperationContext actor;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(); await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(setup);
                setup.Expenses.AddRange(OldExpense("FIRST", "Efectivo"), OldExpense("DURING", "Efectivo"));
                await setup.SaveChangesAsync();
            }
            var service = new ExpenseService(new TestDbContextFactory(options), actor);
            await service.CancelAsync("FIRST", "Before snapshot", "BEFORE-VOID", true);
            var observer = new CancellationQueryObserver(async () => await service.CancelAsync("DURING", "Concurrent", "DURING-VOID", true));
            var reading = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(observer).Options;
            var page = await new ExpenseService(new TestDbContextFactory(reading), actor).GetPageAsync(0, activeShiftOnly: true, cancellationsOnly: true);
            Assert.Equal(1, observer.KeyQueries); Assert.Equal(1, page.TotalCount);
            Assert.Equal("FIRST", Assert.Single(page.Cancellations).Original.Id);
            Assert.Equal(12.34m, page.CancellationAmount); Assert.Equal(-12.34m, page.Summary!.TotalExpenses);
            Assert.Equal(12.34m, page.ActiveShift!.ExpectedBalance);
            var after = await service.GetPageAsync(0, activeShiftOnly: true, cancellationsOnly: true);
            Assert.Equal(2, after.TotalCount); Assert.Equal(24.68m, after.CancellationAmount);
            Assert.Equal(-24.68m, after.Summary!.TotalExpenses); Assert.Equal(24.68m, after.ActiveShift!.ExpectedBalance);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
