using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services;

public sealed record OperationIdentity(string UserId, string RegisterId, string? ShiftId = null);

/// <summary>Server-side authorization and register ownership for every commercial command.</summary>
public sealed class CommercialOperationContext(ISessionManager session, IUserRolePermissionEngine permissions, string registerId)
{
    public string RegisterId { get; } = !string.IsNullOrWhiteSpace(registerId)
        ? registerId : throw new ArgumentException("La caja requiere un identificador.", nameof(registerId));

    public async Task<OperationIdentity> RequireAsync(AppDbContext db, string permission, bool shiftRequired = true)
    {
        var user = session.CurrentCashier;
        var version = session.SessionVersion;
        if (user is null || session.IsTerminalLocked)
            throw new UnauthorizedAccessException("Inicie sesión y desbloquee la caja para continuar.");
        var stored = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        if (stored?.PasswordPolicyVersion < 0)
            throw new UnauthorizedAccessException("Actualice su contraseña desde la pantalla de acceso antes de continuar.");
        if (stored is null || !stored.IsActive || !await permissions.HasPermissionAsync(permissions.MapToModel(stored).DisplayRole, permission))
            throw new UnauthorizedAccessException($"No tiene permiso para esta acción ({permission}).");
        var actor = new OperationIdentity(stored.Id.ToString(), RegisterId);
        EnsureSameSession(user.Id, version);
        if (!shiftRequired) return actor;
        var shift = await db.Shifts.SingleOrDefaultAsync(s => s.RegisterId == RegisterId && s.IsOpen == 1);
        EnsureSameSession(user.Id, version);
        if (shift is null || shift.CashierUserId != actor.UserId)
            throw new InvalidOperationException("Debe abrir un turno propio en esta caja.");
        return actor with { ShiftId = shift.Id };
    }

    private void EnsureSameSession(Guid userId, long version)
    {
        if (session.CurrentCashier?.Id != userId || session.SessionVersion != version || session.IsTerminalLocked)
            throw new UnauthorizedAccessException("La sesión cambió durante la autorización. Repita la acción con su sesión actual.");
    }
}
