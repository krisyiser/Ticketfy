using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Implementations;

public sealed partial class ShiftService
{
    public async Task<CashMovementPage> GetManualMovementPageAsync(int page, int size = 100)
    {
        if (page < 0 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashInOut);
        var shift = await db.Shifts.AsNoTracking().SingleAsync(s => s.Id == actor.ShiftId);
        var query = db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == actor.ShiftId
            && (m.MovementType == MovementType.RetiroEfectivo || m.MovementType == MovementType.IngresoExtraordinario));
        int count = await query.CountAsync();
        var rows = await query.OrderByDescending(m => m.Timestamp).ThenBy(m => m.Id).Skip(offset).Take(size)
            .Select(m => new CashMovementRow(m.Id, m.Amount, m.IsOutflow, m.Description, m.Timestamp,
                m.CashierUserId ?? "Sin atribución", m.RegisterId ?? "Sin atribución", m.ShiftId)).ToListAsync();
        return new(rows, (long)offset + rows.Count < count, count, Map(shift));
    }
}
