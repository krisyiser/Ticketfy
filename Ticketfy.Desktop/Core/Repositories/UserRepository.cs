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

namespace Ticketfy.Core.Repositories;

public class UserRepository : IUserRepository
{
    private static readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset LockedUntil)> PinAttempts = new();
    private readonly AppDbContext _context;
    private readonly IUserRolePermissionEngine _permissionEngine;
    private readonly IAuditService? _auditService;
    private readonly Func<string>? _auditActor;

    public UserRepository(AppDbContext context, IUserRolePermissionEngine? permissionEngine = null, IAuditService? auditService = null, Func<string>? auditActor = null)
    {
        _context = context;
        _permissionEngine = permissionEngine ?? new UserRolePermissionEngine();
        _auditService = auditService;
        _auditActor = auditActor;
    }

    public async Task<List<UserModel>> GetActiveUsersAsync()
    {
        var entities = await _context.Users
            .Where(u => u.IsActive)
            .AsNoTracking()
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    public async Task<UserModel?> ValidatePinAsync(string username, string pin4Digits)
    {
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
            await UpgradePinIfNeededAsync(entity, pin4Digits);
            PinAttempts.TryRemove(attemptKey, out _);
            return MapToModel(entity);
        }

        RegisterPinFailure(attemptKey);
        return null;
    }

    public async Task<UserModel?> ValidateAnyPinAsync(string pin4Digits)
    {
        const string attemptKey = "ANY";
        if (IsPinLocked(attemptKey)) return null;
        var entities = await _context.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var match = entities.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.PinCode) && SecurityManager.VerifyPin(pin4Digits, u.PinCode));
        if (match != null) await UpgradePinIfNeededAsync(match, pin4Digits);
        if (match != null) PinAttempts.TryRemove(attemptKey, out _); else RegisterPinFailure(attemptKey);
        return match != null ? MapToModel(match) : null;
    }

    public async Task<bool> ValidateAdminPinAsync(string pin4Digits)
    {
        const string attemptKey = "ADMIN";
        if (IsPinLocked(attemptKey)) return false;
        var activeUsers = await _context.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var match = activeUsers.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.PinCode)
            && SecurityManager.VerifyPin(pin4Digits, u.PinCode)
            && _permissionEngine.IsAdminOrManager(u.RoleString ?? u.Role.ToString()));
        if (match != null) await UpgradePinIfNeededAsync(match, pin4Digits);
        if (match != null) PinAttempts.TryRemove(attemptKey, out _); else RegisterPinFailure(attemptKey);
        return match != null;
    }

    public async Task<bool> HasAnyUsersAsync()
    {
        return await _context.Users.AnyAsync();
    }

    public async Task CreateUserAsync(UserEntity user)
    {
        NormalizeCredentials(user);
        if (_auditService != null) await LogUserChangeAsync(user.Id, AuditActionType.UserCreated, RiskLevel.Warning);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
    }

    public async Task SaveOrUpdateAdminUserAsync(UserEntity user)
    {
        NormalizeCredentials(user);
        var existing = await _context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin || u.Username.ToLower() == user.Username.ToLower() || u.Id == user.Id);
        if (_auditService != null)
            await LogUserChangeAsync(existing?.Id ?? user.Id, existing == null ? AuditActionType.UserCreated : AuditActionType.UserUpdated, RiskLevel.Critical);
        if (existing != null)
        {
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
        await _context.SaveChangesAsync();
    }

    public async Task<UserEntity?> GetByUsernameAsync(string username)
    {
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
    }

    public async Task<UserEntity?> GetAdminUserAsync()
    {
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Role == UserRole.Admin);
    }

    private UserModel MapToModel(UserEntity entity)
    {
        return _permissionEngine.MapToModel(entity);
    }

    private async Task UpgradePinIfNeededAsync(UserEntity entity, string pin)
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
