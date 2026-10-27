using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Services.Security;

internal enum CredentialRole { Any, Manager, Administrator }
internal sealed record CredentialVerification(UserEntity? User = null, bool PasswordChangeRequired = false);

/// <summary>One durable, per-account attempt budget for password, PIN, login and approval.</summary>
internal sealed class CredentialVerifier(IDbContextFactory<AppDbContext> factory, TimeProvider? clock = null, QuickAccessSession? quickAccess = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<UserEntity?> ByUsernameAsync(string username, string secret, bool pin = false) =>
        (await VerifyAsync(username, null, secret, pin, CredentialRole.Any)).User;
    public async Task<UserEntity?> ByIdAsync(Guid id, string secret, bool pin = false, CredentialRole role = CredentialRole.Any) =>
        (await VerifyAsync(null, id, secret, pin, role)).User;
    public Task<CredentialVerification> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null) =>
        VerifyAsync(username, null, password, false, CredentialRole.Any, establishQuickAccess: true, isRequestCurrent: isRequestCurrent);
    public async Task<UserEntity?> VerifyPasswordAsync(string username, string password, bool supervisorOnly, Func<bool>? isRequestCurrent) =>
        (await VerifyAsync(username, null, password, false, supervisorOnly ? CredentialRole.Manager : CredentialRole.Any,
            establishQuickAccess: !supervisorOnly, isRequestCurrent: isRequestCurrent)).User;
    public async Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > AccountInputPolicy.UsernameLimit || !AccountInputPolicy.IsWellFormed(username)) return false;
        PasswordPolicy.Validate(newPassword, username);
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            throw new InvalidOperationException("La nueva contraseña debe ser distinta de la anterior.");
        return (await VerifyAsync(username, null, currentPassword, false, CredentialRole.Any, newPassword)).User is not null;
    }

    private async Task<CredentialVerification> VerifyAsync(string? username, Guid? id, string secret, bool pin,
        CredentialRole requiredRole, string? replacement = null, bool establishQuickAccess = false, Func<bool>? isRequestCurrent = null)
    {
        long quickEpoch = quickAccess?.Epoch ?? -1;
        if (secret is null || secret.Length > AccountInputPolicy.PasswordLimit || username?.Length > AccountInputPolicy.UsernameLimit
            || !AccountInputPolicy.IsWellFormed(secret) || username is not null && !AccountInputPolicy.IsWellFormed(username)) return new();
        await using var db = await factory.CreateDbContextAsync();
        // A SQLite write transaction serializes attempt accounting and rehashing across processes.
        // Lock/contention errors fail the request; they never fall back to unmetered verification.
        await using var tx = await db.Database.BeginTransactionAsync();
        UserEntity? user;
        if (id.HasValue) user = await db.Users.SingleOrDefaultAsync(u => u.Id == id.Value);
        else
        {
            var matches = await AccountInputPolicy.Named(db, username ?? "").Take(2).ToListAsync();
            // Do not pick an arbitrary account from ambiguous legacy identities, even if only one is active.
            user = matches.Count == 1 ? matches[0] : null;
        }
        string subject = user is null ? "unknown-account" : "account:" + user.Id.ToString("N");
        var state = await db.CredentialAttempts.SingleOrDefaultAsync(a => a.Subject == subject);
        if (state is null)
        {
            state = new CredentialAttemptEntity { Subject = subject };
            db.CredentialAttempts.Add(state);
        }
        long now = _clock.GetUtcNow().UtcTicks;
        if (now < state.LastAttemptUtcTicks || now < state.LockedUntilUtcTicks) return new();
        if (state.LockedUntilUtcTicks > 0) { state.Failures = 0; state.LockedUntilUtcTicks = 0; }
        state.LastAttemptUtcTicks = now;
        string role = user is null ? "" : new UserRolePermissionEngine().MapToModel(user).DisplayRole;
        bool eligible = user?.IsActive == true && (requiredRole == CredentialRole.Any
            || role == "ADMINISTRADOR" || requiredRole == CredentialRole.Manager && role is "GERENTE" or "SUPERVISOR");
        string hash = user is null ? "" : pin ? user.PinCode : user.PasswordHash;
        string quickSubject = user is null ? "" : QuickAccessSession.Subject(db, user);
        string quickFingerprint = user is null ? "" : QuickAccessSession.Fingerprint(user);
        bool quickAllowed = !pin || user is not null && quickAccess?.Allows(quickSubject, quickFingerprint) == true;
        bool valid = eligible && (!pin || requiredRole == CredentialRole.Any
            && user!.PasswordPolicyVersion == PasswordPolicy.CurrentVersion && quickAllowed)
            && (pin ? SecurityManager.VerifyPin(secret, hash) : SecurityManager.VerifyPassword(secret, hash));
        bool changeRequired = false;
        if (valid)
        {
            state.Failures = 0;
            if (replacement is not null)
            {
                user!.PasswordHash = AccountInputPolicy.PasswordHash(replacement, user.Username);
                user.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
                user.PasswordHint = string.Empty;
                user.PinCode = string.Empty;
                CommercialLedger.Journal(db, new OperationIdentity(user.Id.ToString(), "local:authentication"),
                    Guid.NewGuid().ToString("N"), "PASSWORD_UPGRADED_BY_OWNER", user.Id.ToString(), 0m,
                    "Contraseña anterior verificada, nueva política aplicada y PIN previo deshabilitado. Sin secretos en el registro.");
            }
            else if (!pin && PasswordPolicy.RejectionReason(secret, user!.Username) is not null)
            {
                changeRequired = true;
                if (user.PasswordPolicyVersion != PasswordPolicy.ChangeRequired)
                {
                    user.PasswordPolicyVersion = PasswordPolicy.ChangeRequired;
                    CommercialLedger.Journal(db, new OperationIdentity(user.Id.ToString(), "local:authentication"),
                        Guid.NewGuid().ToString("N"), "PASSWORD_UPGRADE_REQUIRED", user.Id.ToString(), 0m,
                        "La credencial verificada requiere actualización antes de iniciar sesión. Sin secretos en el registro.");
                }
            }
            else
            {
                if (!pin) user!.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
                if (pin ? SecurityManager.PinNeedsRehash(hash) : SecurityManager.NeedsRehash(hash))
                {
                    if (pin) user!.PinCode = SecurityManager.HashPin(secret);
                    else user!.PasswordHash = SecurityManager.HashPassword(secret);
                }
            }
        }
        else
        {
            state.Failures++;
            if (state.Failures >= 5)
            {
                state.LockedUntilUtcTicks = checked(now + TimeSpan.FromMinutes(5).Ticks);
                CommercialLedger.Journal(db, new OperationIdentity("system:authentication", "local:authentication"),
                    Guid.NewGuid().ToString("N"), "AUTHENTICATION_LOCKED", subject, 0m,
                    "Cinco intentos fallidos; bloqueo de cinco minutos. Sin credenciales en el registro.");
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (valid && !changeRequired && user is not null)
        {
            if (isRequestCurrent?.Invoke() == false) return new();
            if (pin)
            {
                quickAccess!.Rehash(quickEpoch, quickSubject, quickFingerprint, QuickAccessSession.Fingerprint(user));
                if (quickAccess.Epoch != quickEpoch || !quickAccess.Allows(quickSubject, QuickAccessSession.Fingerprint(user))) return new();
            }
            else if (establishQuickAccess && replacement is null)
                quickAccess?.Issue(quickEpoch, quickSubject, QuickAccessSession.Fingerprint(user));
        }
        return new(valid && !changeRequired ? user : null, changeRequired);
    }
}
