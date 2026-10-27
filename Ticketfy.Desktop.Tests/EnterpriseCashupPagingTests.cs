using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.Views.History;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private static readonly DateTime CashupDay = new(2026, 12, 1);
    private async Task SeedCashupPagesAsync()
    {
        await using var db = Db();
        for (int i = 0; i < 205; i++)
            db.Cashups.Add(new CashupEntity
            {
                Id = $"CUT-{i:D3}",
                OperationId = $"CUT-{i:D3}",
                ShiftId = "SHIFT-1",
                RegisterId = "TEST-REGISTER",
                CashierUserId = CommercialTestSession.UserId.ToString(),
                Timestamp = BusinessTime.ToUtc(CashupDay.AddHours(8).AddMinutes(i)).ToString("O"),
                Type = "Parcial"
            });
        db.Cashups.AddRange(
            new CashupEntity { Id = "LEGACY", Timestamp = "01/12/2026 08:00:00", CashierName = "Historic" },
            new CashupEntity { Id = "BAD-DATE", Timestamp = "not-a-date" });
        void Movement(string id, decimal amount, double minutes, bool outflow, MovementType type) =>
            db.ShiftMovements.Add(new ShiftMovementEntity
            {
                Id = id,
                ShiftId = "SHIFT-1",
                RegisterId = "TEST-REGISTER",
                CashierUserId = CommercialTestSession.UserId.ToString(),
                Amount = amount,
                IsOutflow = outflow,
                MovementType = type,
                Timestamp = BusinessTime.ToUtc(CashupDay.AddHours(8).AddMinutes(minutes)).ToString("O")
            });
        Movement("FIRST", 1.23m, 0, false, MovementType.VentaEfectivo);
        Movement("OUT", 0.50m, 1, true, MovementType.GastoOperativo);
        Movement("COLLECTION", 100m, 2, false, MovementType.AbonoCliente);
        Movement("SECOND", 2.34m, 60, false, MovementType.VentaEfectivo);
        Movement("AFTER", 999m, 500, false, MovementType.VentaEfectivo);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CashupPages_KeepExactCutoffTotalsAndUseBoundedQueriesInsteadOfOnePerCut()
    {
        await SeedCashupPagesAsync();
        var queries = new QueryCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(queries).Options;
        var service = new ShiftService(new TestDbContextFactory(options), _actor);
        var all = new List<CashupEntity>();
        for (int page = 0; page < 3; page++)
        {
            queries.Reads = 0;
            var result = await service.GetCashupPageAsync(CashupDay, CashupDay, page);
            Assert.Equal(206, result.TotalCount);
            Assert.Equal(1, result.UnparseableDateCount);
            Assert.Equal(page < 2, result.HasMore);
            Assert.Equal(page < 2 ? 100 : 6, result.Items.Count);
            Assert.InRange(queries.Reads, 1, 6);
            all.AddRange(result.Items);
        }
        Assert.Equal(206, all.Select(c => c.Id).Distinct().Count());
        foreach (var cut in all.Where(c => c.Id.StartsWith("CUT-", StringComparison.Ordinal)))
        {
            int minute = int.Parse(cut.Id[4..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(minute >= 60 ? 3.57m : 1.23m, cut.TotalSales);
            Assert.Equal(minute >= 1 ? 0.50m : 0m, cut.TotalExpenses);
            Assert.Equal("Desglose verificable", cut.BreakdownStatus);
        }
        var legacy = all.Single(c => c.Id == "LEGACY");
        Assert.Null(legacy.TotalSales); Assert.Null(legacy.TotalExpenses);
        Assert.Equal("Sin desglose", legacy.TotalSalesDisplay);
        Assert.Contains("conciliación", legacy.BreakdownStatus);
        Assert.Equal("CUT-000", all[^2].Id);
        Assert.Equal("LEGACY", all[^1].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCashupsAsync(CashupDay, CashupDay));
        Assert.Empty((await service.GetCashupPageAsync(CashupDay, CashupDay, 99)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetCashupPageAsync(CashupDay, CashupDay, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetCashupPageAsync(CashupDay, CashupDay, 0, 201));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetCashupPageAsync(CashupDay, CashupDay, int.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCashupPageAsync(CashupDay.AddDays(1), CashupDay, 0));
        Assert.Empty((await service.GetCashupPageAsync(DateTime.MaxValue.Date, DateTime.MaxValue.Date, 0)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftService(new TestDbContextFactory(_options)).GetCashupPageAsync(CashupDay, CashupDay, 0));
        await using var db = Db();
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCashupPageAsync(CashupDay, CashupDay, 0));
    }

    [Theory]
    [InlineData("unknown-date")]
    [InlineData("wrong-register")]
    [InlineData("wrong-cashier")]
    [InlineData("negative-amount")]
    [InlineData("unknown-type")]
    [InlineData("sale-outflow")]
    public async Task CashupPages_DoNotInventBreakdownWhenMovementEvidenceIsInconsistent(string invalid)
    {
        await SeedCashupPagesAsync();
        await using var db = Db();
        db.ShiftMovements.Add(new ShiftMovementEntity
        {
            Id = "UNVERIFIABLE",
            ShiftId = "SHIFT-1",
            RegisterId = invalid == "wrong-register" ? "OTHER" : "TEST-REGISTER",
            CashierUserId = invalid == "wrong-cashier" ? "OTHER" : CommercialTestSession.UserId.ToString(),
            Timestamp = invalid == "unknown-date" ? "bad-date" : BusinessTime.ToUtc(CashupDay.AddHours(9)).ToString("O"),
            Amount = invalid == "negative-amount" ? -5m : 5m,
            MovementType = invalid == "unknown-type" ? (MovementType)999 : MovementType.VentaEfectivo,
            IsOutflow = invalid == "sale-outflow"
        });
        await db.SaveChangesAsync();
        var page = await new ShiftService(new TestDbContextFactory(_options), _actor).GetCashupPageAsync(CashupDay, CashupDay, 0);
        Assert.True(page.Items.All(c => c.TotalSales is null && c.TotalExpenses is null), "No corte debe presentar como verificable un movimiento inconsistente.");
    }

    [Fact]
    public void CashupInstants_RejectAmbiguousLegacyTimesButHonorExplicitOffsets()
    {
        var zone = BusinessTime.Resolve("America/Tijuana");
        Assert.False(BusinessTime.TryInstant("01/11/2026 01:30:00", out _, zone));
        Assert.False(BusinessTime.TryInstant("08/03/2026 02:30:00", out _, zone));
        Assert.False(BusinessTime.TryInstant("", out _, zone));
        Assert.False(BusinessTime.TryInstant("invalid", out _, zone));
        Assert.True(BusinessTime.TryInstant("2026-11-01T01:30:00-07:00", out var first, zone));
        Assert.True(BusinessTime.TryInstant("2026-11-01T01:30:00-08:00", out var second, zone));
        Assert.Equal(TimeSpan.FromHours(1), second - first);
        Assert.True(BusinessTime.TryInstant("01/12/2026 12:00:00", out var unambiguous, zone));
        Assert.Equal(20, unambiguous.UtcDateTime.Hour);
        Assert.False(BusinessTime.TryInstant("9999-12-31 23:59:59", out _, BusinessTime.Resolve("America/Mexico_City")));
    }

    [Theory]
    [InlineData("missing-shift")]
    [InlineData("missing-cashier")]
    [InlineData("wrong-register")]
    [InlineData("wrong-cashier")]
    public async Task CashupPages_KeepUnattributableHeaderUnverified(string invalid)
    {
        await using var db = Db();
        db.Cashups.Add(new CashupEntity
        {
            Id = "INCOMPLETE-HEADER",
            Timestamp = "01/12/2026 12:00:00",
            ShiftId = invalid == "missing-shift" ? "does-not-exist" : "SHIFT-1",
            RegisterId = invalid == "wrong-register" ? "OTHER" : "TEST-REGISTER",
            CashierUserId = invalid == "missing-cashier" ? null : invalid == "wrong-cashier" ? "OTHER" : CommercialTestSession.UserId.ToString()
        });
        await db.SaveChangesAsync();
        var page = await new ShiftService(new TestDbContextFactory(_options), _actor).GetCashupPageAsync(CashupDay, CashupDay, 0);
        Assert.Null(Assert.Single(page.Items).TotalSales);
    }

    [AvaloniaFact]
    public async Task Ui_CashupResponseAfterLogoutIsIgnoredAndReadFailureClearsOldEvidence()
    {
        await using var db = Db();
        var pending = new TaskCompletionSource<CashupHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shifts = new DeferredShift(Task.FromResult<Ticketfy.Data.Dtos.ShiftDto?>(null)) { CashupRead = () => pending.Task };
        var model = new HistoryViewModel(Sales, null!, shiftService: shifts);
        var loading = model.LoadCashupsAsync();
        model.ClearSessionData();
        pending.SetResult(new([new CashupEntity { Id = "OLD", ClosedCashAmount = 500m }], 1, false, 0));
        await loading;
        Assert.Empty(model.Cashups);
        Assert.Equal(0, model.MatchingCashupCount);
        Assert.Empty(model.CashupFeedbackMessage);
        Assert.False(model.IsLoadingCashups);
        model.Cashups.Add(new CashupEntity { Id = "STALE" });
        shifts.CashupRead = () => Task.FromException<CashupHistoryPage>(new UnauthorizedAccessException("synthetic-private-error"));
        await model.LoadCashupsAsync();
        Assert.Empty(model.Cashups);
        Assert.Contains("No se pudieron", model.CashupFeedbackMessage);
        Assert.DoesNotContain("synthetic-private-error", model.CashupFeedbackMessage);
    }

    [AvaloniaFact]
    public async Task Ui_CashupPagingHasIndependentTotalsFiltersAndSessionBoundary()
    {
        await SeedCashupPagesAsync();
        await using var db = Db();
        var model = new HistoryViewModel(Sales, null!, shiftService: new ShiftService(new TestDbContextFactory(_options), _actor))
        { StartDate = CashupDay, EndDate = CashupDay };
        await model.FetchSalesHistoryCommand.ExecuteAsync(null);
        Assert.Equal(100, model.Cashups.Count);
        Assert.Equal(206, model.MatchingCashupCount);
        Assert.Equal(0m, model.TotalDayRevenue);
        var view = new HistoryCashupsTableView();
        var window = Show(view, model);
        try
        {
            var next = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.NextCashupPageCommand));
            await ((IAsyncRelayCommand)next.Command!).ExecuteAsync(null);
            await model.NextCashupPageCommand.ExecuteAsync(null);
            Assert.Equal(6, model.Cashups.Count);
            Assert.False(model.HasMoreCashups);
            Assert.Contains("sin desglose", model.CashupFeedbackMessage);
            model.ViewCashupDetailCommand.Execute(model.Cashups.First());
            await model.PreviousCashupPageCommand.ExecuteAsync(null);
            Assert.False(model.IsCashupDetailDialogOpen);
            Assert.Null(model.SelectedCashupForDetail);
            model.StartDate = CashupDay.AddDays(-1);
            Assert.Empty(model.Cashups);
            Assert.Equal(0, model.MatchingCashupCount);
            model.ClearSessionData();
            Assert.Empty(model.CashupFeedbackMessage);
            Assert.False(model.HasPreviousCashups);
        }
        finally { window.Close(); }
    }
}
