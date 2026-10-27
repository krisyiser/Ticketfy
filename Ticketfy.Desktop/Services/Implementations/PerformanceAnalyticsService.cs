using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Models;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class PerformanceAnalyticsService : IPerformanceAnalyticsService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public PerformanceAnalyticsService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public PerformanceAnalyticsService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    public async Task<List<CashierProductivityReportModel>> CalculateTrueCashierProductivityAsync(DateTime startDate, DateTime endDate)
    {
        var end = endDate.TimeOfDay == TimeSpan.Zero ? endDate.Date.AddTicks(TimeSpan.TicksPerDay - 1) : endDate;
        if (startDate > end) throw new InvalidOperationException("La fecha inicial no puede ser posterior a la final.");
        var zone = BusinessTime.Zone;
        bool InPeriod(string value) => BusinessTime.TryLocal(value, out var local, zone) && local >= startDate && local <= end;
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.ReportsView, false);
        // Inactive accounts remain attributable; names alone never establish an operator.
        // Read no credentials, ticket JSON, attendance notes or complete document lists.
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.FullName, u.RoleString, u.Role }).ToListAsync();
        var totals = users.ToDictionary(u => u.Id, _ => new ProductivityTotals());
        var unattributed = new ProductivityTotals();
        ProductivityTotals Owner(string? id) => Guid.TryParse(id, out var userId) && totals.TryGetValue(userId, out var found)
            ? found : unattributed;
        await foreach (var sale in db.Sales.AsNoTracking().InCandidateDateRange(s => s.Date, startDate, end)
            .Select(s => new { s.Date, s.CashierUserId, s.Total, s.LedgerVersion, s.IsCancelled }).AsAsyncEnumerable())
        {
            if (!InPeriod(sale.Date)) continue;
            var owner = Owner(sale.CashierUserId);
            owner.Tickets = checked(owner.Tickets + 1);
            if (sale.LedgerVersion > 0 || sale.IsCancelled == 0) owner.NetSales += sale.Total;
        }
        var returns = from r in db.Returns.AsNoTracking()
                      join s in db.Sales.AsNoTracking() on r.OriginalSaleId equals s.Id
                      where s.LedgerVersion > 0
                      select new { r.CreatedAt, r.TotalRefunded, s.CashierUserId };
        await foreach (var returned in returns.InCandidateDateRange(r => r.CreatedAt, startDate, end).AsAsyncEnumerable())
            if (InPeriod(returned.CreatedAt)) Owner(returned.CashierUserId).NetSales -= returned.TotalRefunded;
        DateTime attendanceEnd = endDate.TimeOfDay == TimeSpan.Zero && endDate.Date < DateTime.MaxValue.Date ? endDate.AddDays(1) : end;
        await foreach (var attendance in db.Attendances.AsNoTracking()
            .Where(a => a.CheckInTime < attendanceEnd && (a.CheckOutTime == null || a.CheckOutTime > startDate
                || (a.CheckOutTime < a.CheckInTime && a.CheckInTime >= startDate)))
            .OrderBy(a => a.UserId).ThenBy(a => a.CheckInTime).ThenBy(a => a.Id)
            .Select(a => new { a.UserId, a.Status, a.CheckInTime, a.CheckOutTime }).AsAsyncEnumerable())
        {
            if (!totals.TryGetValue(attendance.UserId, out var owner)) continue;
            if (attendance.Status != Core.Enums.AttendanceStatus.Completed || attendance.CheckOutTime is null
                || attendance.CheckOutTime < attendance.CheckInTime)
            { owner.AttendanceIssues++; continue; }
            DateTime from = attendance.CheckInTime > startDate ? attendance.CheckInTime : startDate;
            DateTime to = attendance.CheckOutTime.Value < attendanceEnd ? attendance.CheckOutTime.Value : attendanceEnd;
            if (to <= from) continue;
            // Union completed intervals so overlap cannot double hours. Overlap still
            // requires review: do not turn an ambiguous denominator into a productivity rate.
            if (owner.LastAttendanceEnd is { } prior && from < prior)
            { owner.AttendanceIssues++; from = prior; }
            if (to > from) owner.CompletedTicks = checked(owner.CompletedTicks + (to - from).Ticks);
            if (owner.LastAttendanceEnd is null || to > owner.LastAttendanceEnd) owner.LastAttendanceEnd = to;
        }
        var list = new List<CashierProductivityReportModel>();
        foreach (var user in users)
        {
            list.Add(Map(totals[user.Id], user.Id.ToString(), user.FullName, user.RoleString ?? user.Role.ToString(), true));
        }
        if (unattributed.Tickets > 0 || unattributed.NetSales != 0)
            list.Add(Map(unattributed, "", "Histórico sin usuario verificable", "Sin atribución", false));
        return list.OrderByDescending(r => r.HasVerifiedHours).ThenByDescending(r => r.TrueSalesPerHour).ThenBy(r => r.FullName).ThenBy(r => r.UserId).ToList();
    }

    private static CashierProductivityReportModel Map(ProductivityTotals totals, string userId, string name, string role, bool attributed)
    {
        decimal hours = totals.CompletedTicks / (decimal)TimeSpan.TicksPerHour;
        bool verified = attributed && hours > 0 && totals.AttendanceIssues == 0;
        return new()
        {
            UserId = userId,
            FullName = name,
            Role = role,
            AttributionVerified = attributed,
            HasVerifiedHours = verified,
            AttendanceIssueCount = totals.AttendanceIssues,
            TotalTicketsProcessed = totals.Tickets,
            GrossSales = totals.NetSales,
            TotalHoursWorked = (double)Math.Round(hours, 2),
            AverageTicketValue = totals.Tickets == 0 ? 0 : CommercialLedger.Money(totals.NetSales / totals.Tickets),
            TrueSalesPerHour = verified ? CommercialLedger.Money(totals.NetSales / hours) : 0m,
            TicketsPerHour = verified ? (double)Math.Round(totals.Tickets / hours, 2) : 0d,
            EstimatedCommission = 0m
        };
    }

    private sealed class ProductivityTotals
    {
        public int Tickets, AttendanceIssues;
        public decimal NetSales;
        public long CompletedTicks;
        public DateTime? LastAttendanceEnd;
    }
}
