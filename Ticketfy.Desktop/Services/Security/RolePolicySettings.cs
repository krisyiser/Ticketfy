using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;

namespace Ticketfy.Services.Security;

internal static class RolePolicySettings
{
    internal const string Prefix = "RolePermissions_";
    internal static bool IsBaseRole(string role) => role is "ADMINISTRADOR" or "GERENTE" or "SUPERVISOR" or "CAJERO" or "VENDEDOR";
    internal static bool IsRoleSetting(string key) => key == "CustomRolesList" || key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    internal static async Task<string[]> ReadRolesAsync(AppDbContext db)
    {
        string? value = await db.Settings.Where(s => s.Key == "CustomRolesList").Select(s => s.Value).SingleOrDefaultAsync();
        return string.IsNullOrWhiteSpace(value) ? [] : ParseRoles(value);
    }

    internal static async Task<(string Key, string Value)> ValidateAsync(AppDbContext db, string key, string value)
    {
        if (value.Length > 65536) throw new InvalidOperationException("La política de roles excede el tamaño permitido.");
        var roles = await ReadRolesAsync(db);
        if (key == "CustomRolesList")
        {
            var requested = ParseRoles(value);
            var removed = roles.Except(requested, StringComparer.Ordinal).ToArray();
            if (removed.Length > 0)
            {
                // SQLite UPPER is ASCII-only; legacy mixed-case Unicode roles must match the permission engine too.
                await foreach (var assigned in db.Users.AsNoTracking().Select(u => u.RoleString).AsAsyncEnumerable())
                    if (removed.Contains(assigned?.Trim().ToUpperInvariant(), StringComparer.Ordinal))
                        throw new InvalidOperationException("No se puede eliminar un rol asignado a usuarios, incluidos los inactivos. Reasigne primero sus cuentas.");
            }
            var removedKeys = removed.Select(r => Prefix + r).ToArray();
            db.Settings.RemoveRange(await db.Settings.Where(s => removedKeys.Contains(s.Key)).ToListAsync());
            return (key, JsonSerializer.Serialize(requested));
        }
        string role = UserAccountPolicy.NormalizeRole(key[Prefix.Length..]);
        if (string.IsNullOrWhiteSpace(key[Prefix.Length..]) || !IsBaseRole(role) && !roles.Contains(role, StringComparer.Ordinal))
            throw new InvalidOperationException("Registre el rol antes de modificar sus permisos.");
        var policy = string.IsNullOrWhiteSpace(value) ? new Dictionary<string, bool>() :
            JsonSerializer.Deserialize<Dictionary<string, bool>>(value) ?? throw new InvalidOperationException("Política inválida.");
        if (policy.Keys.Any(k => !PermissionCatalog.IsKnown(k))) throw new InvalidOperationException("La política contiene permisos desconocidos.");
        if (role == "ADMINISTRADOR" && PermissionCatalog.AdministratorRecovery.Any(p => policy.TryGetValue(p, out bool allowed) && !allowed))
            throw new InvalidOperationException("El administrador debe conservar gestión de usuarios, roles, acceso a configuración y recuperación. Use otro rol para permisos restringidos.");
        return (Prefix + role, JsonSerializer.Serialize(policy));
    }

    private static string[] ParseRoles(string value)
    {
        var raw = JsonSerializer.Deserialize<string[]>(value) ?? throw new InvalidOperationException("Catálogo de roles inválido.");
        if (raw.Length > 100) throw new InvalidOperationException("El catálogo admite hasta 100 roles personalizados.");
        if (raw.Any(r => string.IsNullOrWhiteSpace(r) || r.Trim().Length > 64
            || r.Any(c => !char.IsLetterOrDigit(c) && c is not ' ' and not '_' and not '-')))
            throw new InvalidOperationException("Use nombres de rol de hasta 64 letras, números, espacios, guiones o guiones bajos.");
        var roles = raw.Select(UserAccountPolicy.NormalizeRole).ToArray();
        if (roles.Any(IsBaseRole) || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new InvalidOperationException("Los roles personalizados no pueden duplicarse ni reemplazar roles base.");
        return roles.OrderBy(r => r, StringComparer.Ordinal).ToArray();
    }
}
