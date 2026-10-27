using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Core.Enums;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public class UserService : IUserService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    private readonly IAuditService? _auditService;
    private readonly CredentialVerifier _credentials;
    private readonly QuickAccessSession? _quickAccess;
    public void ClearQuickAccess() => _quickAccess?.Clear();

    public UserService(AppDbContext context, IAuditService? auditService = null, Func<string>? auditActor = null,
        CommercialOperationContext? operations = null, TimeProvider? clock = null, QuickAccessSession? quickAccess = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(context.Options), auditService, auditActor, operations, clock, quickAccess) { }

    public UserService(IDbContextFactory<AppDbContext> factory, IAuditService? auditService = null, Func<string>? auditActor = null,
        CommercialOperationContext? operations = null, TimeProvider? clock = null, QuickAccessSession? quickAccess = null)
    {
        _factory = factory;
        _operations = operations;
        _auditService = auditService;
        _credentials = new(_factory, clock, quickAccess);
        _quickAccess = quickAccess;
    }

    public async Task<List<UserDto>> GetAllAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await RequireAdminAsync(_context, Guid.Empty);
        var entities = await _context.Users.AsNoTracking().Select(u => new UserEntity
        {
            Id = u.Id,
            FullName = u.FullName,
            Username = u.Username,
            Role = u.Role,
            RoleString = u.RoleString,
            IsActive = u.IsActive
        }).ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> AuthenticateAsync(string username, string password)
    {
        return (await SignInAsync(username, password)).User;
    }

    public async Task<SignInResult> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null)
    {
        var result = await _credentials.SignInAsync(username, password, isRequestCurrent);
        return new(result.User is null ? null : MapToDto(result.User), result.PasswordChangeRequired);
    }

    public Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword) =>
        _credentials.UpgradeOwnPasswordAsync(username, currentPassword, newPassword);

    public async Task<bool> VerifyManagerCredentialAsync(string userId, string secret, bool pin = false) =>
        Guid.TryParse(userId, out var id) && await _credentials.ByIdAsync(id, secret, pin, CredentialRole.Manager) is not null;

    public async Task<bool> ConfirmCurrentAdministratorAsync(string password)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await RequireAdminAsync(db, Guid.Empty);
        bool valid = await _credentials.ByIdAsync(Guid.Parse(actor.UserId), password, role: CredentialRole.Administrator) is not null;
        var current = await RequireAdminAsync(db, Guid.Empty);
        return valid && current.UserId == actor.UserId;
    }

    public async Task SaveAsync(string id, string nombre, string username, string rol, string? password, string? pin, string? passwordHint = null)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        Guid guidId = Guid.TryParse(id, out var parsed) ? parsed : Guid.NewGuid();
        nombre = AccountInputPolicy.FullName(nombre);
        username = AccountInputPolicy.Username(username);
        var actor = await RequireAdminAsync(_context, guidId);
        if (await AccountInputPolicy.Named(_context, username).AnyAsync(u => u.Id != guidId))
            throw new InvalidOperationException("El nombre de usuario ya existe.");
        var entity = await _context.Users.FindAsync(guidId);
        bool isNew = entity == null;

        string normalizedRole = UserAccountPolicy.NormalizeRole(rol);
        var enumRole = UserAccountPolicy.StorageRole(normalizedRole);
        if (!await _context.Users.AnyAsync() && enumRole != UserRole.Admin)
            throw new InvalidOperationException("La primera cuenta debe ser un administrador.");
        if (entity?.IsActive == true && UserAccountPolicy.EffectiveRole(entity) == "ADMINISTRADOR" && normalizedRole != "ADMINISTRADOR"
            && !await UserAccountPolicy.ActiveAdministrators(_context).AnyAsync(u => u.Id != guidId))
            throw new InvalidOperationException("Debe conservar un administrador activo.");

        if ((entity is not null && UserAccountPolicy.EffectiveRole(entity) != normalizedRole) || normalizedRole != "CAJERO")
        {
            if (await _context.Users.AnyAsync())
                await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(_context, Ticketfy.Core.Security.PermissionCatalog.UsersRoles, false);
        }
        await UserAccountPolicy.RequireAssignableRoleAsync(_context, normalizedRole, entity);
        if (entity == null)
        {
            entity = new UserEntity
            {
                Id = guidId,
                FullName = nombre,
                Username = username.Trim(),
                Role = enumRole,
                RoleString = normalizedRole,
                PasswordHash = AccountInputPolicy.PasswordHash(password, username),
                PasswordPolicyVersion = PasswordPolicy.CurrentVersion,
                PasswordHint = string.Empty,
                PinCode = AccountInputPolicy.PinHash(pin),
                IsActive = true
            };
            _context.Users.Add(entity);
        }
        else
        {
            if (!string.Equals(entity.Username, username, StringComparison.OrdinalIgnoreCase))
                entity.PasswordPolicyVersion = Math.Min(0, entity.PasswordPolicyVersion);
            entity.FullName = nombre;
            entity.Username = username.Trim();
            entity.Role = enumRole;
            entity.RoleString = normalizedRole;
            if (pin is not null) entity.PinCode = AccountInputPolicy.PinHash(pin);
            if (!string.IsNullOrEmpty(password))
            {
                entity.PasswordHash = AccountInputPolicy.PasswordHash(password, username);
                entity.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
            }
            entity.PasswordHint = string.Empty;
        }
        if (_auditService != null)
        {
            await _auditService.LogAsync(new AuditLogEntity
            {
                UserId = actor.UserId,
                EntityName = "User",
                EntityId = guidId.ToString(),
                ActionType = isNew ? AuditActionType.UserCreated : AuditActionType.UserUpdated,
                RiskLevel = RiskLevel.Warning,
                Reason = "Administrative user account change"
            });
        }
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), isNew ? "USER_CREATED" : "USER_UPDATED", guidId.ToString(), 0, "Administración de usuario");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (Guid.TryParse(id, out var guidId))
        {
            if (Guid.Parse(actor.UserId) == guidId) throw new InvalidOperationException("No puede desactivar su propia sesión.");
            var entity = await _context.Users.FindAsync(guidId);
            if (entity?.IsActive == true)
            {
                await RequireTargetAccessAsync(_context, entity);
                if (UserAccountPolicy.EffectiveRole(entity) == "ADMINISTRADOR" && !await UserAccountPolicy.ActiveAdministrators(_context).AnyAsync(u => u.Id != guidId))
                    throw new InvalidOperationException("Debe conservar un administrador activo.");
                if (_auditService != null)
                {
                    await _auditService.LogAsync(new AuditLogEntity
                    {
                        UserId = actor.UserId,
                        EntityName = "User",
                        EntityId = guidId.ToString(),
                        ActionType = AuditActionType.UserDeleted,
                        RiskLevel = RiskLevel.Critical,
                        Reason = "Administrative user account deletion"
                    });
                }
                entity.IsActive = false;
                CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "USER_INACTIVATED", id, 0, "Baja lógica");
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            }
        }
    }

    public async Task<string?> GetPasswordHintAsync(string username)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await Task.CompletedTask;
        return null;
    }

    public async Task ChangePasswordAsync(string userId, string password)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        await RequireTargetAccessAsync(_context, entity);
        entity.PasswordHash = AccountInputPolicy.PasswordHash(password, entity.Username);
        entity.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "PASSWORD_CHANGED", userId, 0, "Cambio administrativo de credencial");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task ChangePinAsync(string userId, string pin)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        await RequireTargetAccessAsync(_context, entity);
        entity.PinCode = AccountInputPolicy.PinHash(pin);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "PIN_CHANGED", userId, 0, "Cambio administrativo de PIN");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task<OperationIdentity> RequireAdminAsync(AppDbContext db, Guid newUserId)
    {
        if (!await db.Users.AnyAsync() && newUserId != Guid.Empty) return new(newUserId.ToString(), "bootstrap:local");
        return await (_operations ?? throw new UnauthorizedAccessException("Sesión administrativa requerida."))
            .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.UsersManage, false);
    }
    private async Task RequireTargetAccessAsync(AppDbContext db, UserEntity target)
    {
        if (UserAccountPolicy.RequiresRoleManagement(target))
            await (_operations ?? throw new UnauthorizedAccessException())
                .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.UsersRoles, false);
    }

    public async Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false)
    {
        return Guid.TryParse(userId, out var id) && await _credentials.ByIdAsync(id, secret, pin) is not null;
    }

    private static UserDto MapToDto(UserEntity e)
    {
        string roleStr = !string.IsNullOrWhiteSpace(e.RoleString)
            ? e.RoleString
            : e.Role switch
            {
                UserRole.Admin => "ADMINISTRADOR",
                UserRole.Gerente => "GERENTE",
                _ => "CAJERO"
            };
        return new UserDto(e.Id.ToString(), e.Username, e.FullName, roleStr, e.IsActive);
    }
}
