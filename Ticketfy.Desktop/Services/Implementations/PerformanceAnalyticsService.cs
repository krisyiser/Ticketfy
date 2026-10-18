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
    { _factory = new AppDbContextFactoryImpl<AppDbContext>(db.Options); _operations = operations; }

    public async Task<List<CashierProductivityReportModel>> CalculateTrueCashierProductivityAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.ReportsView, false);
        var users = await db.Users.AsNoTracking().ToListAsync();
        var sales = await db.Sales.AsNoTracking().ToListAsync();
        var returns = (await db.Returns.AsNoTracking().ToListAsync()).Where(r => r.CreatedAt.IsInDateRange(startDate, endDate)).ToList();
        var attendance = await db.Attendances.AsNoTracking().ToListAsync();
        var list = new List<CashierProductivityReportModel>();
        foreach (var user in users)
        {
            var all = sales.Where(s => s.CashierUserId == user.Id.ToString()).ToList();
            var period = all.Where(s => s.Date.IsInDateRange(startDate, endDate)).ToList();
            var ids = all.Where(s => s.LedgerVersion > 0).Select(s => s.Id).ToHashSet();
            decimal net = period.Where(s => s.LedgerVersion > 0 || s.IsCancelled == 0).Sum(s => s.Total)
                - returns.Where(r => ids.Contains(r.OriginalSaleId)).Sum(r => r.TotalRefunded);
            double hours = attendance.Where(a => a.UserId == user.Id && a.CheckInTime.ToString("O").IsInDateRange(startDate, endDate)).Sum(a => a.TotalWorkedHours);
            list.Add(new CashierProductivityReportModel
            {
                UserId = user.Id.ToString(),
                FullName = user.FullName,
                Role = user.RoleString ?? user.Role.ToString(),
                TotalTicketsProcessed = period.Count,
                GrossSales = net,
                TotalHoursWorked = Math.Round(hours, 2),
                AverageTicketValue = period.Count == 0 ? 0 : CommercialLedger.Money(net / period.Count),
                TrueSalesPerHour = hours <= 0 ? 0 : CommercialLedger.Money(net / (decimal)hours),
                TicketsPerHour = hours <= 0 ? 0 : Math.Round(period.Count / hours, 2),
                EstimatedCommission = 0m
            });
        }
        return list.OrderByDescending(r => r.TrueSalesPerHour).ToList();
    }
}
