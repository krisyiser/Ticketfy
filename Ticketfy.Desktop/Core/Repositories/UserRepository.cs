using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Security;
using System.Collections.Concurrent;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Core.Security;

namespace Ticketfy.Core.Repositories;

public class UserRepository : IUserRepository
{
    private static readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset LockedUntil)> PinAttempts = new();
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    private readonly IUserRolePermissionEngine _permissionEngine;
    private readonly IAuditService? _auditService;
    private readonly Func<string>? _auditActor;

    public UserRepository(AppDbContext context, IUserRolePermissionEngine? permissionEngine = null, IAuditService? auditService = null, Func<string>? auditActor = null, CommercialOperationContext? operations = null)
    {
        _factory = new AppDbContextFactoryImpl<AppDbContext>(context.Options);
        _operations = operations;
        _permissionEngine = permissionEngine ?? new UserRolePermissionEngine();
        _auditService = auditService;
        _auditActor = auditActor;
    }

    public async Task<List<UserModel>> GetActiveUsersAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entities = await _context.Users
            .Where(u => u.IsActive)
            .AsNoTracking()
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    public async Task<UserModel?> ValidatePinAsync(string username, string pin4Digits)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        string attemptKey = $"USER:{username.Trim().ToUpperInvariant()}";
        if (IsPinLocked(attemptKey)) return null;
        var entity = await _context.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower() || u.FullName.ToLower() == username.ToLower() || u.Id.ToString() == username);

        if (entity == null || !entity.IsActive)
        {
            RegisterPinFailure(attemptKey);
            return null;
        }

        var storedPin = entity.PinCode;
        if (!string.IsNullOrWhiteSpace(storedPin) && SecurityManager.VerifyPin(pin4Digits, storedPin))
        {
            await UpgradePinIfNeededAsync(_context, entity, pin4Digits);
            PinAttempts.TryRemove(attemptKey, out _);
            return MapToModel(entity);
        }

        RegisterPinFailure(attemptKey);
        return null;
    }

    public async Task<UserModel?> ValidateAnyPinAsync(string pin4Digits)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        const string attemptKey = "ANY";
        if (IsPinLocked(attemptKey)) return null;
        var entities = await _context.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var match = entities.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.PinCode) && SecurityManager.VerifyPin(pin4Digits, u.PinCode));
        if (match != null) await UpgradePinIfNeededAsync(_context, match, pin4Digits);
        if (match != null) PinAttempts.TryRemove(attemptKey, out _); else RegisterPinFailure(attemptKey);
        return match != null ? MapToModel(match) : null;
    }

    public async Task<bool> ValidateAdminPinAsync(string pin4Digits)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        const string attemptKey = "ADMIN";
        if (IsPinLocked(attemptKey)) return false;
        var activeUsers = await _context.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var match = activeUsers.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.PinCode)
            && SecurityManager.VerifyPin(pin4Digits, u.PinCode)
            && _permissionEngine.IsAdminOrManager(u.RoleString ?? u.Role.ToString()));
        if (match != null) await UpgradePinIfNeededAsync(_context, match, pin4Digits);
        if (match != null) PinAttempts.TryRemove(attemptKey, out _); else RegisterPinFailure(attemptKey);
        return match != null;
    }

    public async Task<bool> HasAnyUsersAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        return await _context.Users.AnyAsync();
    }

    public async Task CreateUserAsync(UserEntity user)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var actor = await AuthorizeUserChangeAsync(_context, user);
        NormalizeCredentials(user);
        if (_auditService != null) await LogUserChangeAsync(user.Id, AuditActionType.UserCreated, RiskLevel.Warning);
        _context.Users.Add(user);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "USER_CREATED", user.Id.ToString(), 0, "Alta de usuario");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SaveOrUpdateAdminUserAsync(UserEntity user)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var actor = await AuthorizeUserChangeAsync(_context, user);
        NormalizeCredentials(user);
        var existing = await _context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin || u.Username.ToLower() == user.Username.ToLower() || u.Id == user.Id);
        if (_auditService != null)
            await LogUserChangeAsync(existing?.Id ?? user.Id, existing == null ? AuditActionType.UserCreated : AuditActionType.UserUpdated, RiskLevel.Critical);
        if (existing != null)
        {
            user.Id = existing.Id;
            existing.FullName = user.FullName;
            existing.Username = user.Username;
            existing.PasswordHash = user.PasswordHash;
            existing.PasswordHint = string.Empty;
            existing.PinCode = user.PinCode;
            existing.IsActive = true;
            _context.Users.Update(existing);
        }
        else
        {
            _context.Users.Add(user);
        }
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "ADMIN_SAVED", user.Id.ToString(), 0, "Configuración administrativa");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<UserEntity?> GetByUsernameAsync(string username)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
    }

    public async Task<UserEntity?> GetAdminUserAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Role == UserRole.Admin);
    }

    private async Task<OperationIdentity> AuthorizeUserChangeAsync(AppDbContext db, UserEntity user)
    {
        if (string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.FullName))
            throw new InvalidOperationException("Nombre y usuario requeridos.");
        if (!await db.Users.AnyAsync())
        {
            if (user.Role != UserRole.Admin || string.IsNullOrWhiteSpace(user.PasswordHash))
                throw new InvalidOperationException("La primera cuenta debe ser un administrador protegido.");
            return new(user.Id.ToString(), "bootstrap:local");
        }
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión administrativa requerida."))
            .RequireAsync(db, PermissionCatalog.UsersManage, false);
        if (user.Role != UserRole.Cajero) await _operations.RequireAsync(db, PermissionCatalog.UsersRoles, false);
        if (await db.Users.AnyAsync(u => u.Id != user.Id && u.Username.ToLower() == user.Username.ToLower()))
            throw new InvalidOperationException("El usuario ya existe.");
        return actor;
    }

    private UserModel MapToModel(UserEntity entity)
    {
        return _permissionEngine.MapToModel(entity);
    }

    private static async Task UpgradePinIfNeededAsync(AppDbContext _context, UserEntity entity, string pin)
    {
        if (!SecurityManager.PinNeedsRehash(entity.PinCode)) return;
        entity.PinCode = SecurityManager.HashPin(pin);
        await _context.SaveChangesAsync();
    }

    private static void NormalizeCredentials(UserEntity user)
    {
        user.PasswordHint = string.Empty;
        if (!string.IsNullOrWhiteSpace(user.PasswordHash) && !SecurityManager.IsPasswordHash(user.PasswordHash))
            user.PasswordHash = SecurityManager.HashPassword(user.PasswordHash);
        if (!string.IsNullOrWhiteSpace(user.PinCode) && !SecurityManager.IsPasswordHash(user.PinCode))
            user.PinCode = SecurityManager.HashPin(user.PinCode);
    }

    private static bool IsPinLocked(string key) =>
        PinAttempts.TryGetValue(key, out var state) && state.LockedUntil > DateTimeOffset.UtcNow;

    private static void RegisterPinFailure(string key)
    {
        PinAttempts.AddOrUpdate(key,
            _ => (1, DateTimeOffset.MinValue),
            (_, state) =>
            {
                int attempts = state.LockedUntil > DateTimeOffset.UtcNow ? state.Attempts : state.Attempts + 1;
                return attempts >= 5 ? (0, DateTimeOffset.UtcNow.AddMinutes(5)) : (attempts, DateTimeOffset.MinValue);
            });
    }

    private Task LogUserChangeAsync(Guid id, AuditActionType action, RiskLevel risk) => _auditService!.LogAsync(new AuditLogEntity
    {
        UserId = _auditActor?.Invoke() ?? "SYSTEM",
        EntityName = "User",
        EntityId = id.ToString(),
        ActionType = action,
        RiskLevel = risk,
        Reason = "Administrative user account change"
    });
}
