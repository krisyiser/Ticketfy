using Ticketfy.Core.Enums;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Security;

/// <summary>One effective role for persistence, authorization and last-administrator protection.</summary>
internal static class UserAccountPolicy
{
    private static readonly UserRolePermissionEngine Roles = new();

    internal static string NormalizeRole(string? role)
    {
        if (int.TryParse(role, out _))
            throw new InvalidOperationException("Seleccione un rol por nombre, no un identificador numérico.");
        return Roles.NormalizeRoleName(role);
    }

    internal static string EffectiveRole(UserEntity user) => NormalizeRole(Roles.MapToModel(user).DisplayRole);
    internal static bool RequiresRoleManagement(UserEntity user) => EffectiveRole(user) != "CAJERO";
    internal static async Task RequireAssignableRoleAsync(AppDbContext db, string role, UserEntity? existing = null)
    {
        // Preserve legacy profiles without silently changing their role; new assignments need a registered role.
        if (RolePolicySettings.IsBaseRole(role) || existing is not null && EffectiveRole(existing) == role) return;
        if (!(await RolePolicySettings.ReadRolesAsync(db)).Contains(role, StringComparer.Ordinal))
            throw new InvalidOperationException("Registre el rol personalizado antes de asignarlo a una cuenta.");
    }
    internal static UserRole StorageRole(string role) => role switch
    {
        "ADMINISTRADOR" => UserRole.Admin,
        "GERENTE" or "SUPERVISOR" => UserRole.Gerente,
        _ => UserRole.Cajero
    };

    internal static IQueryable<UserEntity> ActiveAdministrators(AppDbContext db) => db.Users.Where(u => u.IsActive
        && ((u.RoleString == null || u.RoleString.Trim() == "") && u.Role == UserRole.Admin
            || u.RoleString != null && (u.RoleString.Trim().ToUpper() == "ADMIN" || u.RoleString.Trim().ToUpper() == "ADMINISTRADOR")));
}
