using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("rows")]
    [InlineData("shift")]
    public async Task ReconciliationRead_ExcessiveEvidenceIsUnknownNotZeroOrBalanced(string damage)
    {
        await Sales.SaveAsync(Sale("RECONCILIATION-BOUNDS"));
        await using var db = Db();
        if (damage == "text")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE shift_movements SET description = {new string('é', 2 * 1024 * 1024)}");
        else if (damage == "shift")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE shifts SET endTime = {new string(' ', 2 * 1024 * 1024)}");
        else
            await db.Database.ExecuteSqlRawAsync("""
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < 50001)
                INSERT INTO account_entries (Id,PostingSequence,AccountKind,AccountId,OperationId,DocumentId,EntryType,Amount,ShiftId,RegisterId,CashierUserId,CreatedAtUtc)
                SELECT 'L-'||x,100000+x,2,'WALLET','LOAD-'||x,'WALLET','CASH_LOAD',100,'SHIFT-1','TEST-REGISTER','user','2026-01-01' FROM n;
                """);
        var before = await ReturnStateAsync();
        var report = Assert.Single(await Reconciliation.ReconcileAsync(BusinessTime.Today));
        Assert.False(report.IsBalanced); Assert.Null(report.ExpectedCash); Assert.Null(report.Payments); Assert.Null(report.Difference);
        Assert.Equal("READ_BUDGET", Assert.Single(report.Issues).Code);
        Assert.Equal(before, await ReturnStateAsync());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().CloseAsync("SHIFT-1", 20m));
        Assert.Contains("READ_BUDGET", failure.Message);
        Assert.Equal(before, await ReturnStateAsync());
        Assert.Equal(1, (await db.Shifts.SingleAsync()).IsOpen);
        Assert.Empty(await db.Cashups.ToListAsync());
    }

    [Fact]
    public async Task ReconciliationRead_DoesNotHydrateSaleJsonAndDoesNotInventACashCount()
    {
        await Sales.SaveAsync(Sale("RECONCILIATION-JSON"));
        var open = Assert.Single(await Reconciliation.ReconcileAsync(BusinessTime.Today));
        Assert.Null(open.CountedCash); Assert.Null(open.Difference); Assert.False(open.IsBalanced);
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = {new string('x', 2 * 1024 * 1024)}");
        var observer = new HistoryEntitiesProbe();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false).AddInterceptors(observer).Options;
        var shifts = new ShiftService(new TestDbContextFactory(options), _actor);
        await shifts.CloseAsync("SHIFT-1", 20m);
        await shifts.CloseAsync("SHIFT-1", 20m);
        Assert.Single(await db.Cashups.ToListAsync());
        var service = new CommercialReconciliationService(new TestDbContextFactory(options), _actor);
        Assert.True(Assert.Single(await service.ReconcileAsync(BusinessTime.Today)).IsBalanced);
        Assert.True(observer.MaximumPayload <= 2);
    }

    [AvaloniaFact]
    public async Task ReconciliationRead_PagesEveryShiftAndClearsUiWhenDayChanges()
    {
        await using (var db = Db())
        {
            for (int i = 0; i < 205; i++) db.Shifts.Add(new ShiftEntity
            { Id = "PAGE-SHIFT-" + i, RegisterId = "REG-" + i, CashierUserId = "u", StartTime = BusinessTime.Today.ToString("yyyy-MM-dd"), IsOpen = 0 });
            db.Shifts.Add(new ShiftEntity { Id = "OLD-SHIFT", StartTime = "2000-01-01", IsOpen = 0 });
            await db.SaveChangesAsync();
        }
        var all = new List<string>();
        for (int p = 0; p < 3; p++)
        {
            var page = await Reconciliation.ReconcilePageAsync(BusinessTime.Today, p);
            Assert.Equal(206, page.TotalCount); Assert.Equal(p < 2, page.HasMore);
            all.AddRange(page.Items.Select(r => r.ShiftId));
        }
        Assert.Equal(206, all.Distinct().Count()); Assert.DoesNotContain("OLD-SHIFT", all);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.ReconcileAsync(BusinessTime.Today));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reconciliation.ReconcilePageAsync(BusinessTime.Today, -1));
        await Assert.ThrowsAsync<OverflowException>(() => Reconciliation.ReconcilePageAsync(BusinessTime.Today, int.MaxValue));
        Assert.Empty((await Reconciliation.ReconcilePageAsync(BusinessTime.Today, 9)).Items);
        var model = EnterpriseControl();
        await model.ReconcileCommand.ExecuteAsync(null);
        Assert.Equal(100, model.Rows.Count); Assert.True(model.HasMoreReconciliation);
        await model.NextReconciliationCommand.ExecuteAsync(null); Assert.True(model.HasPreviousReconciliation);
        await model.NextReconciliationCommand.ExecuteAsync(null); Assert.Equal(6, model.Rows.Count); Assert.False(model.HasMoreReconciliation);
        await model.PreviousReconciliationCommand.ExecuteAsync(null); Assert.Equal(100, model.Rows.Count);
        model.Day = "2000-01-01"; Assert.Empty(model.Rows); Assert.Empty(model.Summary);
        await model.ReconcileCommand.ExecuteAsync(null); Assert.Single(model.Rows);
        model.ClearDirectoryData(); Assert.Empty(model.Rows);
    }

    [AvaloniaFact]
    public async Task ReconciliationRead_LateResponseCannotRestoreAnotherDayOrSession()
    {
        var original = _options;
        foreach (string transition in new[] { "day", "logout", "permission" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new HistoryReadGate(async sql =>
            {
                if (!sql.Contains("Daily reconciliation shifts", StringComparison.Ordinal)) return;
                entered.TrySetResult(); await release.Task;
            });
            _options = new DbContextOptionsBuilder<AppDbContext>(original).AddInterceptors(gate).Options;
            var model = EnterpriseControl(); model.CanReconcile = true;
            var loading = model.ReconcileCommand.ExecuteAsync(null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (transition == "day") model.Day = "2000-01-01";
                else if (transition == "logout") model.ClearDirectoryData();
                else model.CanReconcile = false;
            }
            finally { release.TrySetResult(); }
            await loading; Assert.Empty(model.Rows); Assert.Empty(model.Summary); Assert.False(model.HasMoreReconciliation);
        }
        _options = original;
    }
}
