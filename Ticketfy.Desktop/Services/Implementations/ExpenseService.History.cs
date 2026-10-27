using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public partial class ExpenseService
{
    public async Task<ExpensePageDto> GetPageAsync(int page, int size = 100, DateTime? startDate = null, DateTime? endDate = null,
        string? shiftId = null, bool activeShiftOnly = false, bool cancellationsOnly = false)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        if (activeShiftOnly && (shiftId is not null || startDate.HasValue || endDate.HasValue))
            throw new ArgumentException("El turno activo se consulta por identidad, sin filtros adicionales de fechas o turno.");
        var period = new BusinessDateQuery(startDate ?? DateTime.MinValue, endDate ?? DateTime.MaxValue);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var operations = _operations ?? throw new UnauthorizedAccessException("Sesión requerida.");
        var actor = await operations.RequireAsync(db, PermissionCatalog.ExpensesManage, false);
        bool profitsVisible = await SensitiveReadAccess.CanSeeCostsAsync(db, operations);
        var active = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.IsOpen == 1 && s.RegisterId == actor.RegisterId);
        bool cashVisible = await CanReadCashAsync(db, operations, active, actor.UserId);
        var currentShift = active is null ? null : new ExpenseShiftSnapshot(active.Id, active.StartTime,
            cashVisible ? active.OpeningBalance : null, cashVisible ? active.ExpectedBalance : null);
        if (activeShiftOnly && active is null)
            return new([], 0, false, 0, profitsVisible ? new(0m, 0m, 0m, 0m, 0m) : null, null, cashVisible);
        string? scope = activeShiftOnly ? active!.Id : shiftId;
        if (cancellationsOnly)
        {
            var reversals = await ReadCancellationsAsync(db, period, scope, offset, size, startDate is null && endDate is null);
            var totals = profitsVisible ? (await TotalsAsync(db, period, scope)).Summary() : null;
            return new([], reversals.Count, (long)offset + reversals.Items.Count < reversals.Count, reversals.Unknown, totals, currentShift, cashVisible)
            { Cancellations = reversals.Items, CancellationAmount = reversals.Amount };
        }
        var rows = await ReadRowsAsync(db, period, scope, offset, size, startDate is null && endDate is null);
        var summary = profitsVisible ? (await TotalsAsync(db, period, scope)).Summary() : null;
        return new(rows.Items, rows.TotalCount, rows.HasMore, rows.UnparseableDateCount, summary, currentShift, cashVisible);
    }

    private static async Task<bool> CanReadCashAsync(AppDbContext db, CommercialOperationContext operations, ShiftEntity? shift, string user)
    {
        try
        {
            await operations.RequireAsync(db, PermissionCatalog.PosCheckout, false);
            if (shift is not null && shift.CashierUserId != user) await operations.RequireAsync(db, PermissionCatalog.CashHistory, false);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record ExpenseRows(List<ExpenseDto> Items, int TotalCount, bool HasMore, int UnparseableDateCount);

    private static async Task<ExpenseRows> ReadRowsAsync(AppDbContext db, BusinessDateQuery period, string? shiftId,
        int offset, int size, bool includeUndated)
    {
        int count = 0, unknown = 0;
        // Stream only dates, never descriptions/references for the whole history. An invalid
        // date must remain visible in the unfiltered history and must not acquire an invented date.
        await foreach (var date in db.Expenses.AsNoTracking().Where(e => shiftId == null || e.ShiftId == shiftId)
            .Select(e => e.Date).AsAsyncEnumerable())
        {
            if (!BusinessTime.TryLocal(date, out var local, period.Zone))
            { unknown = checked(unknown + 1); if (includeUndated) count = checked(count + 1); }
            else if (local >= period.Start && local <= period.End) count = checked(count + 1);
        }
        period.RegisterOn(db);
        var rows = await db.Expenses.FromSqlInterpolated($"""
            SELECT * FROM Expenses
            WHERE ({shiftId} IS NULL OR ShiftId = {shiftId})
              AND (({includeUndated} AND ticketfy_business_ticks(Date) IS NULL)
                OR (ticketfy_business_ticks(Date) >= {period.Start.Ticks}
                    AND ticketfy_business_ticks(Date) <= {period.End.Ticks}))
            ORDER BY ticketfy_business_ticks(Date) DESC, Id COLLATE BINARY
            LIMIT {size} OFFSET {offset}
            """).AsNoTracking().ToListAsync();
        return new(rows.Select(Map).ToList(), count, (long)offset + rows.Count < count, unknown);
    }
}
