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

namespace Ticketfy.Services.Implementations;

public class SettingsService : ISettingsService
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
        await using var _context = await _factory.CreateDbContextAsync();
        string permission = key.StartsWith("RolePermissions_", StringComparison.Ordinal) || key == "CustomRolesList"
            ? Ticketfy.Core.Security.PermissionCatalog.UsersRoles
            : key.StartsWith("Tutorial.", StringComparison.Ordinal) ? Ticketfy.Core.Security.PermissionCatalog.PosCheckout
            : Ticketfy.Core.Security.PermissionCatalog.SettingsBusiness;
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida para cambiar la configuración."))
            .RequireAsync(_context, permission, false);
        if ((key is "PaymentApiToken" or "FacturamaApiPassword" or "FacturamaApiUser") && value.Length > 0)
            throw new InvalidOperationException("Las credenciales deben almacenarse en el almacén protegido de Windows.");
        if (key.StartsWith("RolePermissions_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
        {
            var policy = JsonSerializer.Deserialize<Dictionary<string, bool>>(value)
                ?? throw new InvalidOperationException("Política inválida.");
            if (policy.Keys.Any(k => !Ticketfy.Core.Security.PermissionCatalog.IsKnown(k))) throw new InvalidOperationException("La política contiene permisos desconocidos.");
        }
        await SetValueAsync(_context, key, value);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "SETTING_CHANGED", key, 0, "Cambio de configuración; valores omitidos por privacidad");
        await _context.SaveChangesAsync();
    }

    private static async Task SetValueAsync(AppDbContext db, string key, string value)
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting is null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else setting.Value = value;
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var list = await _context.Settings.AsNoTracking().ToListAsync();
        return list.ToDictionary(s => s.Key, s => s.Value);
    }

    public async Task<AppSettings> GetAppSettingsAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        AppSettings settings = new();
        try
        {
            var json = await GetAsync(AppSettingsJsonKey);
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
        var all = await GetAllAsync();

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

    public async Task SaveAppSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.SettingsBusiness, false);
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
