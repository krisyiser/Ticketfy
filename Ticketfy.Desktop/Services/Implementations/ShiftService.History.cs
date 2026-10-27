using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed partial class ShiftService
{
    public async Task<CashupHistoryPage> GetCashupPageAsync(DateTime start, DateTime end, int page, int size = 100)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        var period = new BusinessDateQuery(start, end);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await Operations.RequireAsync(db, PermissionCatalog.CashHistory, false);
        int count = 0, unknown = 0;
        await foreach (var date in db.Cashups.AsNoTracking().InCandidateDateRange(c => c.Timestamp, period.Start, period.End)
            .Select(c => c.Timestamp).AsAsyncEnumerable())
        {
            if (!BusinessTime.TryLocal(date, out var local, period.Zone)) { unknown++; continue; }
            if (local >= period.Start && local <= period.End) count = checked(count + 1);
        }
        period.RegisterOn(db);
        var rows = await db.Cashups.FromSqlInterpolated($"""
            SELECT * FROM cashups
            WHERE (timestamp NOT LIKE '____-__-__%' OR (timestamp >= {period.Lower} AND timestamp < {period.Upper}))
              AND ticketfy_business_ticks(timestamp) >= {period.Start.Ticks}
              AND ticketfy_business_ticks(timestamp) <= {period.End.Ticks}
            ORDER BY ticketfy_business_ticks(timestamp) DESC, id COLLATE BINARY
            LIMIT {size} OFFSET {offset}
            """).AsNoTracking().ToListAsync();
        await PopulateCashupsAsync(db, rows, period.Zone);
        return new(rows, count, (long)offset + rows.Count < count, unknown);
    }

    private static async Task PopulateCashupsAsync(AppDbContext db, List<CashupEntity> cashups, TimeZoneInfo zone)
    {
        foreach (var cashup in cashups) { cashup.TotalSales = null; cashup.TotalExpenses = null; }
        var shiftIds = cashups.Where(c => c.ShiftId is not null).Select(c => c.ShiftId!).Distinct().ToList();
        if (shiftIds.Count == 0) return;
        var shifts = await db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id))
            .Select(s => new { s.Id, s.RegisterId, s.CashierUserId }).ToDictionaryAsync(s => s.Id);
        var groups = new Dictionary<string, CashupAccumulator>();
        foreach (var cashup in cashups)
        {
            if (cashup.ShiftId is null || cashup.RegisterId is null || cashup.CashierUserId is null
                || !shifts.TryGetValue(cashup.ShiftId, out var shift) || shift.RegisterId != cashup.RegisterId || shift.CashierUserId != cashup.CashierUserId
                || !BusinessTime.TryInstant(cashup.Timestamp, out var instant, zone)) continue;
            if (!groups.TryGetValue(shift.Id, out var group)) groups.Add(shift.Id, group = new(cashup.RegisterId, cashup.CashierUserId));
            group.Cutoffs.Add((instant.UtcTicks, cashup));
        }
        if (groups.Count == 0) return;
        foreach (var group in groups.Values) group.Cutoffs.Sort((a, b) => a.At.CompareTo(b.At));
        BusinessDateQuery.RegisterOn(db, zone);
        string keys = JsonSerializer.Serialize(groups.Keys);
        // One ordered stream for all visible shifts, not one query per cashup.
        // AmountInCents deliberately uses the physical Int64 cents representation.
        // No SQLite floating aggregation or loading movement descriptions is needed.
        await foreach (var movement in db.Database.SqlQuery<StoredCashMovement>($"""
            SELECT shift_id AS ShiftId, RegisterId, CashierUserId, amount AS AmountInCents,
                   is_outflow AS IsOutflow, movement_type AS MovementType,
                   ticketfy_instant_ticks(timestamp) AS InstantTicks
            FROM shift_movements
            WHERE shift_id IN (SELECT value FROM json_each({keys}))
            ORDER BY shift_id, InstantTicks
            """).AsAsyncEnumerable())
        {
            var group = groups[movement.ShiftId];
            if (movement.InstantTicks is null || movement.RegisterId != group.RegisterId
                || movement.CashierUserId != group.CashierUserId || movement.AmountInCents <= 0 || !HasValidCashDirection(movement))
            { group.Invalid = true; continue; }
            group.EmitBefore(movement.InstantTicks.Value);
            decimal amount = movement.AmountInCents / 100m;
            if (movement.IsOutflow) group.Outflows += amount;
            else if (movement.MovementType == (int)MovementType.VentaEfectivo) group.Sales += amount;
        }
        foreach (var group in groups.Values) group.Complete();
    }

    private static bool HasValidCashDirection(StoredCashMovement movement) => (MovementType)movement.MovementType switch
    {
        MovementType.GastoOperativo or MovementType.CompraEfectivo or MovementType.DevolucionCliente
            or MovementType.RetiroEfectivo or MovementType.ReembolsoSaldoCliente => movement.IsOutflow,
        MovementType.VentaEfectivo or MovementType.AbonoCliente or MovementType.IngresoExtraordinario
            or MovementType.RecargaMonedero or MovementType.CancelacionGasto or MovementType.ReembolsoProveedor => !movement.IsOutflow,
        _ => false // Opening funds and closing totals are not cash-ledger movements.
    };

    private sealed class CashupAccumulator(string registerId, string cashierUserId)
    {
        public string RegisterId { get; } = registerId;
        public string CashierUserId { get; } = cashierUserId;
        public List<(long At, CashupEntity Cashup)> Cutoffs { get; } = [];
        public decimal Sales, Outflows;
        public bool Invalid;
        private int _next;
        public void EmitBefore(long at)
        {
            while (_next < Cutoffs.Count && Cutoffs[_next].At < at)
            {
                var cashup = Cutoffs[_next++].Cashup;
                cashup.TotalSales = Sales; cashup.TotalExpenses = Outflows;
            }
        }
        public void Complete()
        {
            // DateTime ticks cannot reach Int64.MaxValue, so all cutoffs are emitted.
            EmitBefore(long.MaxValue);
            if (Invalid) foreach (var cutoff in Cutoffs) { cutoff.Cashup.TotalSales = null; cutoff.Cashup.TotalExpenses = null; }
        }
    }

    private sealed class StoredCashMovement
    {
        public string ShiftId { get; set; } = "";
        public string? RegisterId { get; set; }
        public string? CashierUserId { get; set; }
        public long AmountInCents { get; set; }
        public bool IsOutflow { get; set; }
        public int MovementType { get; set; }
        public long? InstantTicks { get; set; }
    }
}
