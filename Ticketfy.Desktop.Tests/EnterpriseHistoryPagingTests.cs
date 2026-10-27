using System.Globalization;
using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task HistoryPages_OrderMixedDatesPreciselyAndKeepWholePeriodTotals()
    {
        await using var db = Db();
        string Utc(DateTime date) => BusinessTime.ToUtc(date).ToString("O", CultureInfo.InvariantCulture);
        db.Sales.AddRange(
            new SaleEntity { Id = "OLDER", OperationId = "OLDER", Date = "30/11/2026 23:59:59", Total = 99m },
            new SaleEntity { Id = "LEGACY", OperationId = "LEGACY", Date = "01/12/2026 08:00:00", Total = 1.11m },
            new SaleEntity { Id = "A", OperationId = "A", Date = Utc(new(2026, 12, 1, 10, 0, 0)), Total = 3.33m },
            new SaleEntity { Id = "B", OperationId = "B", Date = "2026-12-01 10:00:00", Total = 4.44m },
            new SaleEntity { Id = "LATEST", OperationId = "LATEST", Date = "02/12/2026 12:00:00", Total = 2.22m },
            new SaleEntity { Id = "UNKNOWN", OperationId = "UNKNOWN", Date = "unparseable-date", Total = 500m });
        await db.SaveChangesAsync();
        var start = new DateTime(2026, 12, 1);
        var end = new DateTime(2026, 12, 2);
        var first = await Sales.GetHistoryPageAsync(start, end, 0, 2);
        var second = await Sales.GetHistoryPageAsync(start, end, 1, 2);
        Assert.Equal(new[] { "LATEST", "A" }, first.Items.Select(s => s.Id));
        Assert.Equal(new[] { "B", "LEGACY" }, second.Items.Select(s => s.Id));
        Assert.Equal(4, first.TotalCount);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(1, first.UnparseableDateCount);
        Assert.Equal(11.10m, first.Hours.Sum(h => h.GrossAmount));
        Assert.Equal(first.Hours, second.Hours);
        Assert.Equal(7.77m, first.Hours.Single(h => h.Hour == 10).GrossAmount);
        Assert.Equal(new[] { "LATEST", "A", "B", "LEGACY", "OLDER" }, (await Sales.GetHistoryAsync(5)).Select(s => s.Id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sales.GetHistoryAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sales.GetHistoryAsync(5001));
        var exact = await Sales.GetHistoryPageAsync(start.AddHours(10), start.AddHours(10), 0, 2);
        Assert.Equal(2, exact.TotalCount);
        var beyond = await Sales.GetHistoryPageAsync(start, end, 99, 2);
        Assert.Empty(beyond.Items); Assert.False(beyond.HasMore); Assert.Equal(4, beyond.TotalCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sales.GetHistoryPageAsync(start, end, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sales.GetHistoryPageAsync(start, end, 0, 201));
        await Assert.ThrowsAsync<OverflowException>(() => Sales.GetHistoryPageAsync(start, end, int.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.GetHistoryPageAsync(end, start, 0));
        Assert.Empty((await Sales.GetHistoryPageAsync(DateTime.MinValue, DateTime.MinValue, 0)).Items);
        Assert.Empty((await Sales.GetHistoryPageAsync(DateTime.MaxValue.Date, DateTime.MaxValue.Date, 0)).Items);
    }

    [Fact]
    public async Task HistoryPage_ReauthorizesAndRedactsSensitiveDetails()
    {
        await Sales.SaveAsync(Sale("PAGE-REDACTION"));
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SaleService(factory, new CoOccurrenceQueue(), new SpyAuditService())
            .GetHistoryPageAsync(BusinessTime.Today, BusinessTime.Today, 0));
        (await db.Users.SingleAsync()).RoleString = "SUPERVISOR";
        await db.SaveChangesAsync();
        var page = await Sales.GetHistoryPageAsync(BusinessTime.Today, BusinessTime.Today, 0);
        var sale = Assert.Single(page.Items);
        Assert.Equal(0m, sale.TotalCost);
        Assert.All(sale.Items, item => Assert.Equal(0m, item.Cost));
        Assert.Equal(20m, page.Hours.Sum(h => h.GrossAmount));
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Sales.GetHistoryPageAsync(BusinessTime.Today, BusinessTime.Today, 0));
    }

    [AvaloniaFact]
    public async Task HistoryFilter_RefreshesCashierReportAndClearsOldPeriodImmediately()
    {
        await Sales.SaveAsync(Sale("PERIOD-CURRENT"));
        await using var db = Db();
        var yesterday = BusinessTime.Today.AddDays(-1);
        db.Sales.Add(new SaleEntity
        {
            Id = "PERIOD-OLD",
            OperationId = "PERIOD-OLD",
            Date = yesterday.AddHours(12).ToString("yyyy-MM-dd HH:mm:ss"),
            Total = 10m,
            CashierName = "Historical operator",
            CashierUserId = "historical"
        });
        await db.SaveChangesAsync();
        var model = new HistoryViewModel(Sales, null!) { StartDate = yesterday, EndDate = yesterday };
        await model.FetchSalesHistoryCommand.ExecuteAsync(null);
        Assert.Equal(10m, model.TotalDayRevenue);
        Assert.Equal(10m, Assert.Single(model.CashierPerformanceReport).TotalRevenue);
        model.StartDate = BusinessTime.Today;
        Assert.Empty(model.Sales);
        Assert.Empty(model.CashierPerformanceReport);
        Assert.Equal(0m, model.TotalDayRevenue);
        model.EndDate = BusinessTime.Today;
        await model.FetchSalesHistoryCommand.ExecuteAsync(null);
        Assert.Equal(20m, model.TotalDayRevenue);
        Assert.Equal(20m, Assert.Single(model.CashierPerformanceReport).TotalRevenue);
        model.ClearSessionData();
    }

    [AvaloniaFact]
    public async Task HistoryApprovalFromPreviousSession_CannotCancelSaleOrOpenReturn()
    {
        var sale = await Sales.SaveAsync(Sale("HISTORY-LATE-APPROVAL"));
        await using var db = Db();
        var model = new HistoryViewModel(Sales, null!);
        await model.LoadSalesAsync();
        var approvals = new List<Action<bool>>();
        model.OpenSupervisorPinRequested += (_, response) => approvals.Add(response);
        var returnsOpened = 0;
        model.OpenReturnRequested += _ => returnsOpened++;
        await model.CancelSaleCommand.ExecuteAsync(sale);
        model.OpenReturnDialogCommand.Execute(sale);
        Assert.Equal(2, approvals.Count);
        model.ClearSessionData();
        foreach (var approve in approvals) approve(true);
        Assert.Equal(0, returnsOpened);
        Assert.Equal(0, (await db.Sales.AsNoTracking().SingleAsync()).IsCancelled);
        Assert.Empty(await db.Returns.ToListAsync());
        Assert.Empty(model.FeedbackMessage);
        await model.RePrintTicketCommand.ExecuteAsync(sale);
        Assert.Contains("no se envió", model.FeedbackMessage);
    }
}
