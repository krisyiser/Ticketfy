using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Models;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

/// <summary>Attendance belongs to the authenticated person, never a caller-supplied operator.</summary>
public sealed class AttendanceService : IAttendanceService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    private readonly TimeProvider _clock;

    public AttendanceService(AppDbContext db, CommercialOperationContext? operations = null, TimeProvider? clock = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations, clock) { }
    public AttendanceService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations, TimeProvider? clock = null)
    { _factory = factory; _operations = operations; _clock = clock ?? TimeProvider.System; }

    private async Task<OperationIdentity> RequireSelfAsync(AppDbContext db, string userId)
    {
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, PermissionCatalog.PosCheckout, false);
        if (!Guid.TryParse(userId, out var id) || id.ToString() != actor.UserId)
            throw new UnauthorizedAccessException("Sólo puede registrar o consultar su propia asistencia.");
        return actor;
    }

    public async Task<bool> HasActiveClockInAsync(string userId) => await GetActiveAttendanceAsync(userId) is not null;

    public async Task<AttendanceEntity?> GetActiveAttendanceAsync(string userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await RequireSelfAsync(db, userId);
        var id = Guid.Parse(userId);
        return await db.Attendances.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == id
            && a.CheckOutTime == null && a.Status == AttendanceStatus.Active);
    }

    public async Task<AttendanceResultModel> ClockInAsync(string userId, string notes = "")
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireSelfAsync(db, userId);
        var id = Guid.Parse(userId);
        var existing = await db.Attendances.SingleOrDefaultAsync(a => a.UserId == id
            && a.CheckOutTime == null && a.Status == AttendanceStatus.Active);
        if (existing is not null) return new AttendanceResultModel { IsSuccess = true, AttendanceId = existing.Id };
        if (notes.Length > 2000) throw new InvalidOperationException("La nota admite hasta 2000 caracteres.");
        var entry = new AttendanceEntity
        {
            UserId = id,
            CheckInTime = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Helpers.BusinessTime.Zone).DateTime,
            Status = AttendanceStatus.Active,
            TerminalName = actor.RegisterId,
            Notes = notes.Trim()
        };
        db.Attendances.Add(entry);
        CommercialLedger.Journal(db, actor, "ATTENDANCE:IN:" + entry.Id, "ATTENDANCE_IN", entry.Id, 0m, "Entrada registrada por el usuario.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new AttendanceResultModel { IsSuccess = true, AttendanceId = entry.Id };
    }

    public async Task<AttendanceResultModel> ClockOutAsync(string userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireSelfAsync(db, userId);
        var id = Guid.Parse(userId);
        // Another cashier's open register must not prevent this person's departure.
        if (await db.Shifts.AnyAsync(s => s.CashierUserId == actor.UserId && s.IsOpen == 1))
            return new AttendanceResultModel { ErrorMessage = "Realice el corte y cierre sus turnos antes de registrar salida." };
        var entry = await db.Attendances.SingleOrDefaultAsync(a => a.UserId == id
            && a.CheckOutTime == null && a.Status == AttendanceStatus.Active);
        if (entry is null) return new AttendanceResultModel { ErrorMessage = "No tiene una entrada activa. Las anomalías requieren revisión del supervisor." };
        var now = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Helpers.BusinessTime.Zone).DateTime;
        if (now < entry.CheckInTime) throw new InvalidOperationException("La hora de salida es anterior a la entrada. Revise el reloj del equipo.");
        entry.CheckOutTime = now;
        entry.Status = AttendanceStatus.Completed;
        CommercialLedger.Journal(db, actor, "ATTENDANCE:OUT:" + entry.Id, "ATTENDANCE_OUT", entry.Id, 0m, "Salida registrada por el usuario.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new AttendanceResultModel { IsSuccess = true, AttendanceId = entry.Id };
    }

    // Compatibility name: flag uncertainty; never manufacture a checkout or worked hours.
    public async Task AutoCloseForgottenAttendancesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, PermissionCatalog.AttendanceManage, false);
        var cutoff = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Helpers.BusinessTime.Zone).DateTime.AddHours(-24);
        var forgotten = await db.Attendances.Where(a => a.CheckOutTime == null
            && a.CheckInTime < cutoff && a.Status == AttendanceStatus.Active).ToListAsync();
        foreach (var entry in forgotten)
        {
            entry.Status = AttendanceStatus.IncompleteAnomaly;
            CommercialLedger.Journal(db, actor, "ATTENDANCE:ANOMALY:" + entry.Id, "ATTENDANCE_ANOMALY", entry.Id, 0m,
                "Entrada de más de 24 horas sin salida confirmada. Excluida de horas trabajadas.");
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<List<AttendanceEntity>> GetReviewQueueAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.AttendanceManage, false);
        var cutoff = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Helpers.BusinessTime.Zone).DateTime.AddHours(-24);
        return await db.Attendances.AsNoTracking().Where(a => a.Status == AttendanceStatus.IncompleteAnomaly || a.Status == AttendanceStatus.ShiftMismatch
            || (a.Status == AttendanceStatus.Active && a.CheckOutTime == null && a.CheckInTime < cutoff)).OrderBy(a => a.CheckInTime).ToListAsync();
    }

    public async Task ResolveAnomalyAsync(string attendanceId, DateTime confirmedEnd, string reason, string operationId)
    {
        operationId = CommercialLedger.OperationId(operationId);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new InvalidOperationException("Indique el motivo y evidencia de la hora confirmada (hasta 500 caracteres).");
        confirmedEnd = DateTime.SpecifyKind(confirmedEnd, DateTimeKind.Unspecified);
        _ = Core.Helpers.BusinessTime.ToUtc(confirmedEnd); // Reject ambiguous/nonexistent wall-clock input.
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.AttendanceManage, false);
        await CommandIdentity.BindAsync(db, actor, operationId, "ATTENDANCE_REVIEW", attendanceId, confirmedEnd.ToString("O"), reason.Trim());
        if (await db.TransactionJournal.AnyAsync(j => j.OperationId == operationId && j.EventType == "ATTENDANCE_REVIEWED")) return;
        var entry = await db.Attendances.SingleAsync(a => a.Id == attendanceId);
        var now = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Helpers.BusinessTime.Zone).DateTime;
        if (entry.Status == AttendanceStatus.Completed || (entry.Status == AttendanceStatus.Active && entry.CheckInTime >= now.AddHours(-24)))
            throw new InvalidOperationException("La asistencia no es una anomalía pendiente de revisión.");
        if (confirmedEnd < entry.CheckInTime || confirmedEnd > now) throw new InvalidOperationException("La salida confirmada debe estar entre la entrada y la hora actual.");
        var userId = entry.UserId.ToString();
        if (await db.Shifts.AnyAsync(s => s.CashierUserId == userId && s.IsOpen == 1))
            throw new InvalidOperationException("El usuario tiene un turno abierto. Ciérrelo antes de resolver su asistencia.");
        if (await db.Attendances.AnyAsync(a => a.Id != entry.Id && a.UserId == entry.UserId && a.Status == AttendanceStatus.Completed
            && a.CheckInTime < confirmedEnd && a.CheckOutTime > entry.CheckInTime))
            throw new InvalidOperationException("El intervalo se superpone a otra asistencia confirmada; no se duplicarán horas.");
        string previousEnd = entry.CheckOutTime?.ToString("O") ?? "sin confirmar";
        entry.CheckOutTime = confirmedEnd;
        entry.Status = AttendanceStatus.Completed;
        CommercialLedger.Journal(db, actor, operationId, "ATTENDANCE_REVIEWED", entry.Id, 0m,
            $"Salida anterior: {previousEnd}; salida confirmada: {confirmedEnd:O}; motivo: {reason.Trim()}");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
