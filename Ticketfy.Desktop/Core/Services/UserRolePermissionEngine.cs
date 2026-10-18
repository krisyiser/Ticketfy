using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Core.Enums;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Core.Security;

namespace Ticketfy.Core.Services;

/// <summary>
/// Specialized Engine for managing User accounts, System & Custom Roles,
/// RBAC Permission Evaluation, and Session Authorization.
/// Acts as the single source of truth for user role mapping and security validation.
/// </summary>
public class UserRolePermissionEngine : IUserRolePermissionEngine
{
    private readonly ISettingsService? _settingsService;

    public UserRolePermissionEngine(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public string NormalizeRoleName(string? rawRole)
    {
        if (string.IsNullOrWhiteSpace(rawRole)) return "CAJERO";
        string norm = rawRole.Trim().ToUpperInvariant();
        return norm switch
        {
            "ADMIN" or "ADMINISTRADOR" => "ADMINISTRADOR",
            "GERENTE" => "GERENTE",
            "SUPERVISOR" => "SUPERVISOR",
            "CAJERO" => "CAJERO",
            "VENDEDOR" => "VENDEDOR",
            _ => norm
        };
    }

    public bool IsAdminOrManager(string roleName)
    {
        string norm = NormalizeRoleName(roleName);
        return norm is "ADMINISTRADOR" or "GERENTE" or "SUPERVISOR";
    }

    public UserModel MapToModel(UserEntity entity)
    {
        string roleStr = !string.IsNullOrWhiteSpace(entity.RoleString)
            ? NormalizeRoleName(entity.RoleString)
            : entity.Role switch
            {
                UserRole.Admin => "ADMINISTRADOR",
                UserRole.Gerente => "GERENTE",
                _ => "CAJERO"
            };

        var systemRole = IsAdminOrManager(roleStr) ? SystemRole.ADMIN : SystemRole.CAJERO;

        return new UserModel
        {
            Id = entity.Id,
            FullName = entity.FullName,
            Username = entity.Username,
            Role = systemRole,
            RoleString = roleStr,
            Pin4Digits = string.Empty,
            IsActive = entity.IsActive
        };
    }

    public UserModel MapDtoToModel(UserDto dto, string? pinCode = null)
    {
        string roleStr = NormalizeRoleName(dto.Role);
        var systemRole = IsAdminOrManager(roleStr) ? SystemRole.ADMIN : SystemRole.CAJERO;

        return new UserModel
        {
            Id = Guid.TryParse(dto.Id, out var parsedGuid) ? parsedGuid : Guid.NewGuid(),
            FullName = dto.FullName,
            Username = dto.Username,
            Role = systemRole,
            RoleString = roleStr,
            Pin4Digits = string.Empty,
            IsActive = dto.IsActive
        };
    }

    public async Task<bool> HasPermissionAsync(string roleName, string permissionKey)
    {
        if (!PermissionCatalog.IsKnown(permissionKey)) return false;
        string normRole = NormalizeRoleName(roleName);

        var perms = await GetRolePermissionsAsync(normRole);
        if (perms.TryGetValue(permissionKey, out bool isGranted))
        {
            return isGranted;
        }

        return PermissionCatalog.IsGrantedByDefault(normRole, permissionKey);
    }

    public async Task<Dictionary<string, bool>> GetRolePermissionsAsync(string roleName)
    {
        string normRole = NormalizeRoleName(roleName);
        if (_settingsService == null) return [];

        try
        {
            var json = await _settingsService.GetAsync($"RolePermissions_{normRole}");
            if (!string.IsNullOrWhiteSpace(json))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (dict != null) return dict;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UserRolePermissionEngine: Error loading permissions for role {Role}", normRole);
        }

        return [];
    }
}
