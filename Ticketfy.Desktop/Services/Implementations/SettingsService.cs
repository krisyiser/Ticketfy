using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public partial class SettingsService : ISettingsService
{
    private const string AppSettingsJsonKey = "SystemGlobalAppSettings_V4";
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;

    public SettingsService(AppDbContext context, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(context.Options), operations) { }
    public SettingsService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    public async Task<string?> GetAsync(string key)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var setting = await _context.Settings.FindAsync(key);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        if (key is AppSettingsJsonKey or "Inventory.DefaultMinStock" or InitialSetupStateStore.Key or "IsOnboardingCompleted") throw new InvalidOperationException("Use el contrato de configuración correspondiente; no se permiten reemplazos desde una clave genérica.");
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        string permission = RolePolicySettings.IsRoleSetting(key)
            ? Ticketfy.Core.Security.PermissionCatalog.UsersRoles
            : key.StartsWith("Printer", StringComparison.Ordinal) || key.StartsWith("Scanner", StringComparison.Ordinal) || key.StartsWith("Scale", StringComparison.Ordinal)
                ? Ticketfy.Core.Security.PermissionCatalog.SettingsHardware
            : key.StartsWith("Tutorial.", StringComparison.Ordinal) ? Ticketfy.Core.Security.PermissionCatalog.PosCheckout
            : Ticketfy.Core.Security.PermissionCatalog.SettingsBusiness;
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida para cambiar la configuración."))
            .RequireAsync(_context, permission, false);
        if ((key is "PaymentApiToken" or "FacturamaApiPassword" or "FacturamaApiUser") && value.Length > 0)
            throw new InvalidOperationException("Las credenciales deben almacenarse en el almacén protegido de Windows.");
        if (RolePolicySettings.IsRoleSetting(key))
            (key, value) = await RolePolicySettings.ValidateAsync(_context, key, value);
        await SetValueAsync(_context, key, value);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "SETTING_CHANGED", key, 0, "Cambio de configuración; valores omitidos por privacidad");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task SetValueAsync(AppDbContext db, string key, string value)
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting is null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else setting.Value = value;
    }

    public Task AddCustomRoleAsync(string name) => ChangeCustomRoleAsync(name, false);
    public Task DeleteCustomRoleAsync(string name) => ChangeCustomRoleAsync(name, true);
    private async Task ChangeCustomRoleAsync(string name, bool remove)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Indique el nombre del rol.");
        string role = UserAccountPolicy.NormalizeRole(name);
        if (RolePolicySettings.IsBaseRole(role))
            throw new InvalidOperationException("Los roles base no se agregan ni eliminan.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.UsersRoles, false);
        var current = await RolePolicySettings.ReadRolesAsync(db);
        if (current.Contains(role, StringComparer.Ordinal) != remove) return;
        var requested = remove ? current.Where(r => r != role) : current.Append(role);
        var change = await RolePolicySettings.ValidateAsync(db, "CustomRolesList", JsonSerializer.Serialize(requested));
        await SetValueAsync(db, change.Key, change.Value);
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), remove ? "ROLE_DELETED" : "ROLE_CREATED", role, 0m, "Administración de roles");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var list = await _context.Settings.AsNoTracking().ToListAsync();
        return list.ToDictionary(s => s.Key, s => s.Value);
    }

    public async Task<AppSettings> GetAppSettingsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await ReadAppSettingsAsync(db);
    }

    private static async Task<AppSettings> ReadAppSettingsAsync(AppDbContext db)
    {
        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        AppSettings settings = new();
        try
        {
            all.TryGetValue(AppSettingsJsonKey, out var json);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
                if (deserialized != null) settings = deserialized;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed deserializing AppSettings from DB, returning defaults");
        }

        // Always overlay individual keys for 100% full integration with Onboarding & Settings
        if (all.TryGetValue("CurrentTheme", out var ct)) settings.Visual.ThemeName = ct;
        if (all.TryGetValue("AccentColor", out var ac)) settings.Visual.PrimaryColor = ac;
        if (all.TryGetValue("AppFont", out var af)) settings.Visual.FontFamily = af;
        if (all.TryGetValue("SidebarPosition", out var sp)) settings.Visual.SidebarPosition = sp;

        if (all.TryGetValue("EmpresaNombreComercial", out var nc) && !string.IsNullOrWhiteSpace(nc)) settings.Company.CommercialName = nc;
        else if (all.TryGetValue("BusinessName", out var bn) && !string.IsNullOrWhiteSpace(bn)) settings.Company.CommercialName = bn;

        if (all.TryGetValue("EmpresaEmailContacto", out var ec) && !string.IsNullOrWhiteSpace(ec)) settings.Company.Email = ec;
        else if (all.TryGetValue("BusinessEmail", out var be) && !string.IsNullOrWhiteSpace(be)) settings.Company.Email = be;

        if (all.TryGetValue("EmpresaTelefonoFijo", out var tf) && !string.IsNullOrWhiteSpace(tf)) settings.Company.Phone = tf;
        else if (all.TryGetValue("BusinessPhone", out var bp) && !string.IsNullOrWhiteSpace(bp)) settings.Company.Phone = bp;

        if (all.TryGetValue("EmpresaCalleYNumero", out var cn) && !string.IsNullOrWhiteSpace(cn)) settings.Company.Address = cn;
        else if (all.TryGetValue("BusinessAddress", out var ba) && !string.IsNullOrWhiteSpace(ba)) settings.Company.Address = ba;

        if (all.TryGetValue("EmpresaRfc", out var rfc) && !string.IsNullOrWhiteSpace(rfc)) settings.Company.Rfc = rfc;

        return settings;
    }

    public Task SaveAppSettingsAsync(AppSettings settings) => SaveSettingsCoreAsync(settings, null);
    public Task SaveSectionAsync(AppSettings settings, AppSettingsSection section) => SaveSettingsCoreAsync(settings, section);

    private async Task SaveSettingsCoreAsync(AppSettings settings, AppSettingsSection? section)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        string permission = section switch
        {
            AppSettingsSection.Security => Ticketfy.Core.Security.PermissionCatalog.SettingsSecurity,
            AppSettingsSection.Hardware => Ticketfy.Core.Security.PermissionCatalog.SettingsHardware,
            null or AppSettingsSection.Visual => Ticketfy.Core.Security.PermissionCatalog.SettingsBusiness,
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, permission, false);
        var previousSettings = await ReadAppSettingsAsync(db);
        if (section.HasValue)
        {
            var merged = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(previousSettings))!;
            switch (section.Value)
            {
                case AppSettingsSection.Visual: merged.Visual = settings.Visual; break;
                case AppSettingsSection.Hardware: merged.Hardware = settings.Hardware; merged.Ticket = settings.Ticket; break;
                case AppSettingsSection.Security: merged.System = settings.System; merged.Company.TimeZoneId = settings.Company.TimeZoneId; break;
            }
            settings = merged;
        }
        if (JsonSerializer.Serialize(previousSettings.System) != JsonSerializer.Serialize(settings.System))
            await _operations.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.SettingsSecurity, false);
        if (JsonSerializer.Serialize(previousSettings.Hardware) != JsonSerializer.Serialize(settings.Hardware)
            || JsonSerializer.Serialize(previousSettings.Ticket) != JsonSerializer.Serialize(settings.Ticket))
            await _operations.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.SettingsHardware, false);
        _ = Ticketfy.Core.Helpers.BusinessTime.Resolve(settings.Company.TimeZoneId);
        if (!settings.System.EnableAuditLogs || settings.System.AutoLockTimeoutMinutes is < 1 or > 60
            || settings.System.BackupIntervalHours is < 1 or > 168 || settings.System.BackupRetentionCount is < 1 or > 365
            || settings.System.BackupRetentionDays is < 1 or > 3650 || !Enum.IsDefined(settings.System.TelemetryConsent))
            throw new InvalidOperationException("Revise los límites de bloqueo, respaldos y telemetría. La auditoría comercial no puede desactivarse.");
        if (previousSettings.Company.TimeZoneId != settings.Company.TimeZoneId)
        {
            if (await db.Shifts.AnyAsync(s => s.IsOpen == 1))
                throw new InvalidOperationException("Cierre todos los turnos antes de cambiar la zona horaria comercial. Reinicie después de guardar.");
            CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "BUSINESS_TIME_ZONE_CHANGED", AppSettingsJsonKey, 0,
                previousSettings.Company.TimeZoneId + " -> " + settings.Company.TimeZoneId);
        }
        var values = new Dictionary<string, string>
        {
            [AppSettingsJsonKey] = JsonSerializer.Serialize(settings),
            ["CurrentTheme"] = settings.Visual.ThemeName,
            ["AccentColor"] = settings.Visual.PrimaryColor,
            ["AppFont"] = settings.Visual.FontFamily,
            ["SidebarPosition"] = settings.Visual.SidebarPosition,
            ["EmpresaNombreComercial"] = settings.Company.CommercialName,
            ["BusinessName"] = settings.Company.CommercialName,
            ["EmpresaEmailContacto"] = settings.Company.Email,
            ["BusinessEmail"] = settings.Company.Email,
            ["EmpresaTelefonoFijo"] = settings.Company.Phone,
            ["BusinessPhone"] = settings.Company.Phone,
            ["EmpresaCalleYNumero"] = settings.Company.Address,
            ["BusinessAddress"] = settings.Company.Address,
            ["EmpresaRfc"] = settings.Company.Rfc
        };
        foreach (var pair in values) await SetValueAsync(db, pair.Key, pair.Value ?? "");
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "APP_SETTINGS_UPDATED", AppSettingsJsonKey, 0, "Configuración actualizada atómicamente");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
