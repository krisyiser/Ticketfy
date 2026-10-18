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
    private readonly Func<string>? _auditActor;

    public UserService(AppDbContext context, IAuditService? auditService = null, Func<string>? auditActor = null, CommercialOperationContext? operations = null)
    {
        _factory = new AppDbContextFactoryImpl<AppDbContext>(context.Options);
        _operations = operations;
        _auditService = auditService;
        _auditActor = auditActor;
    }

    public async Task<int> GetCountAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        return await _context.Users.CountAsync();
    }

    public async Task<List<UserDto>> GetAllAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entities = await _context.Users.AsNoTracking().ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetByNameAsync(string name)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        // Explicitly ONLY match the exact Username credential, never the FullName
        var entity = await _context.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == name.ToLower());
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<List<UserDto>> GetManagersAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entities = await _context.Users.Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Gerente).AsNoTracking().ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task SaveAsync(string id, string nombre, string username, string rol, string? passwordHash, string? pinHash, string? passwordHint = null)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        Guid guidId = Guid.TryParse(id, out var parsed) ? parsed : Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Nombre y usuario requeridos.");
        var actor = await RequireAdminAsync(_context, guidId);
        if (await _context.Users.AnyAsync(u => u.Id != guidId && u.Username.ToLower() == username.Trim().ToLower()))
            throw new InvalidOperationException("El nombre de usuario ya existe.");
        var entity = await _context.Users.FindAsync(guidId);
        bool isNew = entity == null;

        var enumRole = ParseRole(rol);
        if (!await _context.Users.AnyAsync() && enumRole != UserRole.Admin)
            throw new InvalidOperationException("La primera cuenta debe ser un administrador.");
        string normalizedRole = string.IsNullOrWhiteSpace(rol)
            ? (enumRole == UserRole.Admin ? "ADMINISTRADOR" : (enumRole == UserRole.Gerente ? "GERENTE" : "CAJERO"))
            : (rol.Trim().Equals("ADMIN", StringComparison.OrdinalIgnoreCase) ? "ADMINISTRADOR" : rol.Trim().ToUpperInvariant());

        if ((entity is not null && entity.RoleString != normalizedRole) || enumRole != UserRole.Cajero)
        {
            if (await _context.Users.AnyAsync())
                await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(_context, Ticketfy.Core.Security.PermissionCatalog.UsersRoles, false);
        }
        if (entity == null)
        {
            if (string.IsNullOrWhiteSpace(passwordHash)) throw new InvalidOperationException("La cuenta requiere contraseña.");
            entity = new UserEntity
            {
                Id = guidId,
                FullName = nombre,
                Username = username,
                Role = enumRole,
                RoleString = normalizedRole,
                PasswordHash = NormalizePassword(passwordHash),
                PasswordHint = string.Empty,
                PinCode = NormalizePin(pinHash),
                IsActive = true
            };
            _context.Users.Add(entity);
        }
        else
        {
            entity.FullName = nombre;
            entity.Username = username.Trim();
            entity.Role = enumRole;
            entity.RoleString = normalizedRole;
            if (!string.IsNullOrEmpty(pinHash)) entity.PinCode = NormalizePin(pinHash);
            if (!string.IsNullOrEmpty(passwordHash)) entity.PasswordHash = NormalizePassword(passwordHash);
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

    private static UserRole ParseRole(string rol)
    {
        if (string.IsNullOrWhiteSpace(rol)) return UserRole.Cajero;
        string norm = rol.Trim().ToUpperInvariant();
        if (norm == "ADMINISTRADOR" || norm == "ADMIN") return UserRole.Admin;
        if (norm == "GERENTE" || norm == "SUPERVISOR") return UserRole.Gerente;
        if (Enum.TryParse<UserRole>(rol, true, out var parsedRole)) return parsedRole;
        return UserRole.Cajero;
    }

    public async Task DeleteAsync(string id)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (Guid.TryParse(id, out var guidId))
        {
            if (actor.UserId == id) throw new InvalidOperationException("No puede desactivar su propia sesión.");
            var entity = await _context.Users.FindAsync(guidId);
            if (entity != null)
            {
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
                if (entity.Role == UserRole.Admin && await _context.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin) <= 1)
                    throw new InvalidOperationException("Debe conservar un administrador activo.");
                entity.IsActive = false;
                CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "USER_INACTIVATED", id, 0, "Baja lógica");
                await _context.SaveChangesAsync();
            }
        }
    }

    public async Task<string?> GetPasswordHashAsync(string userId)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        if (Guid.TryParse(userId, out var guidId))
        {
            var entity = await _context.Users.FindAsync(guidId);
            return entity?.PasswordHash;
        }
        return null;
    }

    public async Task<string?> GetPinHashAsync(string userId)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        if (Guid.TryParse(userId, out var guidId))
        {
            var entity = await _context.Users.FindAsync(guidId);
            return entity?.PinCode;
        }
        return null;
    }

    public async Task<string?> GetPasswordHintAsync(string username)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await Task.CompletedTask;
        return null;
    }

    public async Task UpdatePasswordHashAsync(string userId, string passwordHash)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new InvalidOperationException("Contraseña requerida.");
        entity.PasswordHash = NormalizePassword(passwordHash);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "PASSWORD_CHANGED", userId, 0, "Cambio administrativo de credencial");
        await _context.SaveChangesAsync();
    }

    public async Task UpdatePinHashAsync(string userId, string pinHash)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await RequireAdminAsync(_context, Guid.Empty);
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        entity.PinCode = NormalizePin(pinHash);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "PIN_CHANGED", userId, 0, "Cambio administrativo de PIN");
        await _context.SaveChangesAsync();
    }

    private async Task<OperationIdentity> RequireAdminAsync(AppDbContext db, Guid newUserId)
    {
        if (!await db.Users.AnyAsync() && newUserId != Guid.Empty) return new(newUserId.ToString(), "bootstrap:local");
        return await (_operations ?? throw new UnauthorizedAccessException("Sesión administrativa requerida."))
            .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.UsersManage, false);
    }

    public async Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false)
    {
        if (!Guid.TryParse(userId, out var id)) return false;
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.IsActive);
        if (user is null) return false;
        string hash = pin ? user.PinCode : user.PasswordHash;
        if (pin ? !SecurityManager.VerifyPin(secret, hash) : !SecurityManager.VerifyPassword(secret, hash)) return false;
        if (pin ? SecurityManager.PinNeedsRehash(hash) : SecurityManager.NeedsRehash(hash))
        {
            if (pin) user.PinCode = SecurityManager.HashPin(secret); else user.PasswordHash = SecurityManager.HashPassword(secret);
            await db.SaveChangesAsync();
        }
        return true;
    }

    private static string NormalizePassword(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        SecurityManager.IsPasswordHash(value) ? value : SecurityManager.HashPassword(value);

    private static string NormalizePin(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        SecurityManager.IsPasswordHash(value) ? value : SecurityManager.HashPin(value);

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
