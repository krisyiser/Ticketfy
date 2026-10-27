using Microsoft.EntityFrameworkCore;
using Avalonia.Headless.XUnit;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Models;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task Productivity_PreservesUnattributedHistoricalRevenueWithoutInventingAnOperator()
    {
        await using var db = Db();
        db.Sales.AddRange(
            new SaleEntity { Id = "KNOWN", OperationId = "KNOWN", Date = "18/10/2026 12:00:00", Total = 10m, CashierUserId = CommercialTestSession.UserId.ToString().ToUpperInvariant() },
            new SaleEntity { Id = "UNKNOWN", OperationId = "UNKNOWN", Date = "18/10/2026 13:00:00", Total = 5m, CashierUserId = "admin", CashierName = "admin" },
            new SaleEntity { Id = "NO-USER", OperationId = "NO-USER", Date = "18/10/2026 14:00:00", Total = 3m });
        await db.SaveChangesAsync();
        var reports = await new PerformanceAnalyticsService(db, _actor).CalculateTrueCashierProductivityAsync(new(2026, 10, 18), new(2026, 10, 18));
        Assert.Equal(18m, reports.Sum(r => r.GrossSales));
        Assert.Equal(3, reports.Sum(r => r.TotalTicketsProcessed));
        var known = reports.Single(r => r.UserId == CommercialTestSession.UserId.ToString());
        Assert.Equal(10m, known.GrossSales);
        var unknown = reports.Single(r => r.UserId == "");
        Assert.Equal(8m, unknown.GrossSales);
        Assert.DoesNotContain("admin", unknown.FullName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, unknown.EstimatedCommission);
        Assert.Equal(0m, unknown.TrueSalesPerHour);
        Assert.False(unknown.AttributionVerified);
        Assert.Equal("No atribuible", unknown.EstimatedCommissionDisplay);
        var summary = await Sales.GetCashierPerformanceReportAsync(new(2026, 10, 18), new(2026, 10, 18), 5m);
        Assert.Equal(18m, summary.Sum(r => r.TotalRevenue));
        Assert.Equal(0.50m, summary.Single(r => r.AttributionVerified).EarnedCommissionAmount);
        Assert.Equal(0m, summary.Single(r => !r.AttributionVerified).EarnedCommissionAmount);
        Assert.Equal("No atribuible", summary.Single(r => !r.AttributionVerified).EarnedCommissionDisplay);
    }

    [Fact]
    public async Task Productivity_DoesNotDoubleCountOverlappingAttendanceOrCalculateUnverifiedHourlyRates()
    {
        await using var db = Db();
        var day = new DateTime(2026, 10, 18);
        db.Sales.Add(new SaleEntity { Id = "ATTENDANCE", OperationId = "ATTENDANCE", Date = "18/10/2026 12:00:00", Total = 10m, CashierUserId = CommercialTestSession.UserId.ToString() });
        db.Attendances.AddRange(
            new AttendanceEntity { Id = "ONE", UserId = CommercialTestSession.UserId, Status = AttendanceStatus.Completed, CheckInTime = day.AddHours(8), CheckOutTime = day.AddHours(10) },
            new AttendanceEntity { Id = "TWO", UserId = CommercialTestSession.UserId, Status = AttendanceStatus.Completed, CheckInTime = day.AddHours(9), CheckOutTime = day.AddHours(11) },
            new AttendanceEntity { Id = "UNRESOLVED", UserId = CommercialTestSession.UserId, Status = AttendanceStatus.IncompleteAnomaly, CheckInTime = day.AddHours(14) });
        await db.SaveChangesAsync();
        var report = Assert.Single(await new PerformanceAnalyticsService(db, _actor).CalculateTrueCashierProductivityAsync(day, day));
        Assert.Equal(3d, report.TotalHoursWorked);
        Assert.Equal(0m, report.TrueSalesPerHour);
        Assert.Equal(0d, report.TicketsPerHour);
        Assert.Equal(2, report.AttendanceIssueCount);
        Assert.False(report.HasVerifiedHours);
        Assert.Equal("No calculable", report.TrueSalesPerHourDisplay);
    }

    [Fact]
    public async Task Productivity_ClipsHoursExactlyAndAttributesLaterReturnsToOriginalInactiveOperator()
    {
        var saved = await Sales.SaveAsync(Sale("PRODUCTIVITY-RETURN"));
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Return", operationId: "PRODUCTIVITY-RETURNED");
        await using var db = Db();
        var day = BusinessTime.Today;
        // Test fixtures may move immutable records explicitly through SQL to model historical instants.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET date={day.AddDays(-1).AddHours(12).ToString("yyyy-MM-dd HH:mm:ss")} WHERE id={saved.Id}");
        db.Attendances.Add(new AttendanceEntity
        {
            UserId = CommercialTestSession.UserId,
            Status = AttendanceStatus.Completed,
            CheckInTime = day.AddHours(8),
            CheckOutTime = day.AddHours(12)
        });
        await db.SaveChangesAsync();
        var service = new PerformanceAnalyticsService(db, _actor);
        var report = Assert.Single(await service.CalculateTrueCashierProductivityAsync(day, day));
        Assert.Equal(-10m, report.GrossSales);
        Assert.Equal(0, report.TotalTicketsProcessed);
        Assert.Equal(4d, report.TotalHoursWorked);
        Assert.Equal(-2.50m, report.TrueSalesPerHour);
        Assert.True(report.HasVerifiedHours);
        var clipped = Assert.Single(await service.CalculateTrueCashierProductivityAsync(day.AddHours(9), day.AddHours(10.5)));
        Assert.Equal(1.5d, clipped.TotalHoursWorked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateTrueCashierProductivityAsync(day.AddHours(12), day.AddHours(11)));
        Assert.Equal(0d, Assert.Single(await service.CalculateTrueCashierProductivityAsync(DateTime.MaxValue.Date, DateTime.MaxValue.Date)).TotalHoursWorked);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PerformanceAnalyticsService(db).CalculateTrueCashierProductivityAsync(day, day));
        (await db.Users.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        var manager = CommercialTestSession.Create(db, false, "SUPERVISOR-REPORT", Guid.NewGuid(), "GERENTE");
        var after = await new PerformanceAnalyticsService(db, manager).CalculateTrueCashierProductivityAsync(day, day);
        Assert.Equal(-10m, after.Single(r => r.UserId == CommercialTestSession.UserId.ToString()).GrossSales);
        Assert.DoesNotContain(after, r => !r.AttributionVerified);
    }

    [AvaloniaFact]
    public async Task Ui_ProductivityUsesWholeBusinessDatesReactiveCommissionsAndClearsInvalidatedResults()
    {
        var pending = new TaskCompletionSource<List<CashierProductivityReportModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DeferredProductivity(() => pending.Task);
        var model = new CashierPerformanceViewModel(service);
        var loading = model.LoadReportsAsync();
        var day = new DateTime(2026, 10, 18);
        model.StartDate = new DateTimeOffset(day.AddHours(15), TimeSpan.FromHours(4));
        model.EndDate = new DateTimeOffset(day.AddHours(19), TimeSpan.FromHours(4));
        pending.SetResult([new() { FullName = "Stale", GrossSales = 500m }]);
        await loading;
        Assert.Empty(model.CashierProductivityReports);
        var attributed = new CashierProductivityReportModel { FullName = "Operator", GrossSales = 20m, AttributionVerified = true };
        var unknown = new CashierProductivityReportModel { FullName = "Historical", GrossSales = 10m, AttributionVerified = false };
        service.Read = () => Task.FromResult(new List<CashierProductivityReportModel> { attributed, unknown });
        var view = new CashierPerformanceView();
        var window = Show(view, model);
        try
        {
            await ClickTwiceAsync(view, model.LoadReportsCommand);
            Assert.Equal(day, service.Start); Assert.Equal(day, service.End);
            var notifications = new List<string?>();
            attributed.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            model.CommissionPercentage = 10m;
            Assert.Equal(2m, attributed.EstimatedCommission);
            Assert.Equal(0m, unknown.EstimatedCommission);
            Assert.Contains(nameof(CashierProductivityReportModel.EstimatedCommissionDisplay), notifications);
            model.CommissionPercentage = 101m;
            Assert.Equal(0m, attributed.EstimatedCommission);
            Assert.Contains("0 y 100", model.FeedbackMessage);
            await model.LoadReportsAsync();
            Assert.Empty(model.CashierProductivityReports);
            model.CommissionPercentage = 5m;
            await model.LoadReportsAsync();
            model.StartDate = model.StartDate!.Value.AddDays(-1);
            Assert.Empty(model.CashierProductivityReports);
            model.ClearSessionData();
            Assert.Equal(0m, model.CommissionPercentage);
        }
        finally { window.Close(); }
    }

    private sealed class DeferredProductivity(Func<Task<List<CashierProductivityReportModel>>> read) : IPerformanceAnalyticsService
    {
        public Func<Task<List<CashierProductivityReportModel>>> Read = read;
        public DateTime Start, End;
        public Task<List<CashierProductivityReportModel>> CalculateTrueCashierProductivityAsync(DateTime startDate, DateTime endDate)
        { Start = startDate; End = endDate; return Read(); }
    }
}
