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
    private readonly AppDbContext _context;
    private readonly IAuditService? _auditService;
    private readonly Func<string>? _auditActor;

    public UserService(AppDbContext context, IAuditService? auditService = null, Func<string>? auditActor = null)
    {
        _context = context;
        _auditService = auditService;
        _auditActor = auditActor;
    }

    public async Task<int> GetCountAsync()
    {
        return await _context.Users.CountAsync();
    }

    public async Task<List<UserDto>> GetAllAsync()
    {
        var entities = await _context.Users.AsNoTracking().ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetByNameAsync(string name)
    {
        // Explicitly ONLY match the exact Username credential, never the FullName
        var entity = await _context.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == name.ToLower());
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<List<UserDto>> GetManagersAsync()
    {
        var entities = await _context.Users.Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Gerente).AsNoTracking().ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task SaveAsync(string id, string nombre, string username, string rol, string? passwordHash, string? pinHash, string? passwordHint = null)
    {
        Guid guidId = Guid.TryParse(id, out var parsed) ? parsed : Guid.NewGuid();
        var entity = await _context.Users.FindAsync(guidId);
        bool isNew = entity == null;

        var enumRole = ParseRole(rol);
        string normalizedRole = string.IsNullOrWhiteSpace(rol)
            ? (enumRole == UserRole.Admin ? "ADMINISTRADOR" : (enumRole == UserRole.Gerente ? "GERENTE" : "CAJERO"))
            : (rol.Trim().Equals("ADMIN", StringComparison.OrdinalIgnoreCase) ? "ADMINISTRADOR" : rol.Trim().ToUpperInvariant());

        if (entity == null)
        {
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
                UserId = _auditActor?.Invoke() ?? "SYSTEM",
                EntityName = "User",
                EntityId = guidId.ToString(),
                ActionType = isNew ? AuditActionType.UserCreated : AuditActionType.UserUpdated,
                RiskLevel = RiskLevel.Warning,
                Reason = "Administrative user account change"
            });
        }
        await _context.SaveChangesAsync();
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
        if (Guid.TryParse(id, out var guidId))
        {
            var entity = await _context.Users.FindAsync(guidId);
            if (entity != null)
            {
                if (_auditService != null)
                {
                    await _auditService.LogAsync(new AuditLogEntity
                    {
                        UserId = _auditActor?.Invoke() ?? "SYSTEM",
                        EntityName = "User",
                        EntityId = guidId.ToString(),
                        ActionType = AuditActionType.UserDeleted,
                        RiskLevel = RiskLevel.Critical,
                        Reason = "Administrative user account deletion"
                    });
                }
                _context.Users.Remove(entity);
                await _context.SaveChangesAsync();
            }
        }
    }

    public async Task<string?> GetPasswordHashAsync(string userId)
    {
        if (Guid.TryParse(userId, out var guidId))
        {
            var entity = await _context.Users.FindAsync(guidId);
            return entity?.PasswordHash;
        }
        return null;
    }

    public async Task<string?> GetPinHashAsync(string userId)
    {
        if (Guid.TryParse(userId, out var guidId))
        {
            var entity = await _context.Users.FindAsync(guidId);
            return entity?.PinCode;
        }
        return null;
    }

    public async Task<string?> GetPasswordHintAsync(string username)
    {
        await Task.CompletedTask;
        return null;
    }

    public async Task UpdatePasswordHashAsync(string userId, string passwordHash)
    {
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        entity.PasswordHash = passwordHash;
        await _context.SaveChangesAsync();
    }

    public async Task UpdatePinHashAsync(string userId, string pinHash)
    {
        if (!Guid.TryParse(userId, out var guidId)) return;
        var entity = await _context.Users.FindAsync(guidId);
        if (entity == null) return;
        entity.PinCode = pinHash;
        await _context.SaveChangesAsync();
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
