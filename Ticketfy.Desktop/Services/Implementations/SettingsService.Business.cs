using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Security;

namespace Ticketfy.Services.Implementations;

public partial class SettingsService
{
    // Only company metadata belongs in this command; credentials, roles, hardware and
    // time-zone changes have separate authorization and persistence contracts.
    private static readonly HashSet<string> BusinessSettingKeys = new(StringComparer.Ordinal)
    {
        "EmpresaNombreComercial", "EmpresaRazonSocial", "EmpresaGiroComercial", "EmpresaEslogan",
        "EmpresaMonedaPrincipal", "EmpresaRfc", "EmpresaRegimenFiscal", "EmpresaCodigoPostalFiscal",
        "EmpresaUsoCfdiPorDefecto", "EmpresaCertificadoCerPath", "EmpresaCertificadoKeyPath",
        "EmpresaNombreSucursal", "EmpresaCalleYNumero", "EmpresaColonia", "EmpresaCiudadMunicipio",
        "EmpresaEstado", "EmpresaTelefonoFijo", "EmpresaWhatsappContacto", "EmpresaEmailContacto",
        "EmpresaSitioWeb", "EmpresaFacebook", "EmpresaInstagram", "EmpresaMensajeBienvenidaTicket",
        "EmpresaQrRedesUrl", "EmpresaLogoPrincipalUrl", "EmpresaLogoIsotipoUrl", "EmpresaLogoTermicoUrl",
        "EmpresaThermalLogoThreshold"
    };

    public async Task SaveBusinessSettingsAsync(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is 0 || values.Count > BusinessSettingKeys.Count)
            throw new InvalidOperationException("Indique los datos del negocio que desea guardar.");
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!BusinessSettingKeys.Contains(pair.Key))
                throw new InvalidOperationException("La solicitud contiene una opción ajena a los datos del negocio.");
            string text = (pair.Value ?? "").Trim();
            int limit = pair.Key == "EmpresaNombreComercial" ? 256 : pair.Key == "EmpresaEmailContacto" ? 254 : 512;
            if (text.Length > limit || text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                throw new InvalidOperationException("Los datos del negocio contienen texto demasiado largo o caracteres no admitidos.");
            if (pair.Key == "EmpresaNombreComercial" && text.Length == 0)
                throw new InvalidOperationException("Indique el nombre del negocio.");
            if (pair.Key is "EmpresaTelefonoFijo" or "EmpresaWhatsappContacto" && text.Length > 0
                && (text.Length != 10 || !text.All(char.IsAsciiDigit)))
                throw new InvalidOperationException("Los teléfonos deben contener diez dígitos.");
            if (pair.Key == "EmpresaEmailContacto" && text.Length > 0
                && (!MailAddress.TryCreate(text, out var mail) || mail.Address != text))
                throw new InvalidOperationException("Revise el correo electrónico del negocio.");
            if (pair.Key == "EmpresaThermalLogoThreshold" && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
                || !double.IsFinite(threshold) || threshold is < 0 or > 255))
                throw new InvalidOperationException("El umbral del logotipo debe estar entre 0 y 255.");
            if (pair.Key is "EmpresaCertificadoCerPath" or "EmpresaCertificadoKeyPath" && !LocalDevelopmentMode.FacturamaEnabled)
                throw new InvalidOperationException(LocalDevelopmentMode.FacturamaUnavailableMessage);
            changes.Add(pair.Key, text);
        }

        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida para guardar el negocio."))
            .RequireAsync(db, PermissionCatalog.SettingsBusiness, false);
        var settings = await ReadAppSettingsAsync(db);
        var company = settings.Company;
        if (changes.TryGetValue("EmpresaNombreComercial", out var name)) { company.CommercialName = name; changes["BusinessName"] = name; }
        if (changes.TryGetValue("EmpresaEmailContacto", out var email)) { company.Email = email; changes["BusinessEmail"] = email; }
        if (changes.TryGetValue("EmpresaTelefonoFijo", out var phone)) { company.Phone = phone; changes["BusinessPhone"] = phone; }
        if (changes.TryGetValue("EmpresaCalleYNumero", out var address)) { company.Address = address; changes["BusinessAddress"] = address; }
        if (changes.TryGetValue("EmpresaRazonSocial", out var legal)) company.LegalName = legal;
        if (changes.TryGetValue("EmpresaRfc", out var rfc)) company.Rfc = rfc;
        if (changes.TryGetValue("EmpresaRegimenFiscal", out var regime)) company.FiscalRegime = regime;
        if (changes.TryGetValue("EmpresaCodigoPostalFiscal", out var zip)) company.ZipCode = zip;
        if (changes.TryGetValue("EmpresaSitioWeb", out var website)) company.Website = website;
        if (changes.TryGetValue("EmpresaLogoPrincipalUrl", out var logo)) company.LogoPath = logo;
        changes[AppSettingsJsonKey] = JsonSerializer.Serialize(settings);
        var current = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        if (changes.All(p => current.TryGetValue(p.Key, out var value) && value == p.Value)) return;
        foreach (var pair in changes) await SetValueAsync(db, pair.Key, pair.Value);
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "BUSINESS_SETTINGS_UPDATED", "Company", 0,
            "Datos del negocio actualizados atómicamente; valores omitidos por privacidad");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
