using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Services.Security;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Manages all business/company identity fields: general info, identity, fiscal,
/// branch address, and social networks. Previously embedded in the monolithic SettingsViewModel.
/// </summary>
public partial class EmpresaSettingsViewModel : ObservableObject
{
    private readonly SecureCredentialStore _credentialStore = new();
    private readonly ISettingsService? _settingsService;

    // ── Generales ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaNombreComercial = string.Empty;
    [ObservableProperty] private string _empresaRazonSocial = string.Empty;
    [ObservableProperty] private string _empresaGiroComercial = string.Empty;
    [ObservableProperty] private string _empresaEslogan = string.Empty;
    [ObservableProperty] private string _empresaMonedaPrincipal = string.Empty;
    [ObservableProperty] private string _empresaSimboloMoneda = "$";
    [ObservableProperty] private string _empresaZonaHoraria = string.Empty;

    public ObservableCollection<string> EmpresaGiroOptions { get; } = [
        "Abarrotes / Minisuper", "Restaurante / Cafetería", "Boutique / Ropa",
        "Farmacia", "Ferretería", "Servicios / General", "Otro"
    ];
    public ObservableCollection<string> MonedaOptions { get; } = ["MXN", "USD", "EUR", "GTQ"];
    public ObservableCollection<string> ZonaHorariaOptions { get; } = [
        "America/Mexico_City", "America/Tijuana", "America/Cancun", "America/Monterrey"
    ];

    // ── Identidad ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaLogoPrincipalUrl = "";
    [ObservableProperty] private string _empresaLogoIsotipoUrl = "";
    [ObservableProperty] private string _empresaLogoTermicoUrl = "";
    [ObservableProperty] private string _empresaColorCorporativoHex = "#2563EB";
    [ObservableProperty] private bool _empresaSincronizarColorSistema = true;
    [ObservableProperty] private double _empresaThermalLogoThreshold = 128.0;

    // ── Fiscal ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaRfc = string.Empty;
    [ObservableProperty] private string _empresaRegimenFiscal = string.Empty;
    [ObservableProperty] private string _empresaCodigoPostalFiscal = string.Empty;
    [ObservableProperty] private string _empresaCertificadoCerPath = "";
    [ObservableProperty] private string _empresaCertificadoKeyPath = "";
    [ObservableProperty] private string _empresaPasswordKeyCsd = "";
    [ObservableProperty] private string _empresaUsoCfdiPorDefecto = string.Empty;
    [ObservableProperty] private string _empresaPrefijoFolioInterno = "F-";
    [ObservableProperty] private int _empresaFolioInicial = 1;
    public string CsdValidityStatus => "NO VERIFICADO";
    public bool IsFiscalIntegrationEnabled => Ticketfy.Core.Configuration.LocalDevelopmentMode.FacturamaEnabled;
    [ObservableProperty] private string _facturamaApiUser = string.Empty;
    [ObservableProperty] private string _facturamaApiPassword = string.Empty;
    [ObservableProperty] private string _facturamaAmbiente = "Sandbox";

    public ObservableCollection<string> FacturamaAmbienteOptions { get; } = ["Sandbox", "Producción"];
    public ObservableCollection<string> RegimenFiscalOptions { get; } = [
        "601 - General de Ley Personas Morales",
        "612 - Personas Físicas con Actividades Empresariales y Profesionales",
        "626 - Régimen Simplificado de Confianza (RESICO)",
        "605 - Sueldos y Salarios e Ingresos Asimilados a Salarios",
        "616 - Sin obligaciones fiscales"
    ];
    public ObservableCollection<string> UsoCfdiOptions { get; } = [
        "G01 - Adquisición de mercancías", "G03 - Gastos en general",
        "S01 - Sin efectos fiscales", "P01 - Por definir"
    ];

    // ── Sucursal ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaNombreSucursal = string.Empty;
    [ObservableProperty] private string _empresaCalleYNumero = string.Empty;
    [ObservableProperty] private string _empresaColonia = string.Empty;
    [ObservableProperty] private string _empresaCiudadMunicipio = string.Empty;
    [ObservableProperty] private string _empresaEstado = string.Empty;
    [ObservableProperty] private string _empresaTelefonoFijo = string.Empty;
    [ObservableProperty] private string _empresaWhatsappContacto = string.Empty;

    public ObservableCollection<string> MexicanStates { get; } = [
        "Aguascalientes", "Baja California", "Baja California Sur", "Campeche", "Chiapas",
        "Chihuahua", "Coahuila", "Colima", "Ciudad de México", "Durango", "Guanajuato",
        "Guerrero", "Hidalgo", "Jalisco", "Estado de México", "Michoacán", "Morelos",
        "Nayarit", "Nuevo León", "Oaxaca", "Puebla", "Querétaro", "Quintana Roo",
        "San Luis Potosí", "Sinaloa", "Sonora", "Tabasco", "Tamaulipas", "Tlaxcala",
        "Veracruz", "Yucatán", "Zacatecas"
    ];

    // ── Redes sociales ─────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaEmailContacto = string.Empty;
    [ObservableProperty] private string _empresaSitioWeb = string.Empty;
    [ObservableProperty] private string _empresaFacebook = string.Empty;
    [ObservableProperty] private string _empresaInstagram = string.Empty;
    [ObservableProperty] private string _empresaTiktok = string.Empty;
    [ObservableProperty] private string _empresaMensajeBienvenidaTicket = string.Empty;
    [ObservableProperty] private string _empresaQrRedesUrl = string.Empty;

    // ── Sub-tab navigation ─────────────────────────────────────────────────
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSubEmpresaGenerales))] private bool _isSubGenerales = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSubEmpresaIdentidad))] private bool _isSubIdentidad = false;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSubEmpresaFiscal))] private bool _isSubFiscal = false;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSubEmpresaSucursal))] private bool _isSubSucursal = false;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSubEmpresaRedes))] private bool _isSubRedes = false;

    public bool IsSubEmpresaGenerales => IsSubGenerales;
    public bool IsSubEmpresaIdentidad => IsSubIdentidad;
    public bool IsSubEmpresaFiscal => IsSubFiscal;
    public bool IsSubEmpresaSucursal => IsSubSucursal;
    public bool IsSubEmpresaRedes => IsSubRedes;

    [RelayCommand] private void SelectEmpresaSubTab(string tab) => SelectSubTab(tab);

    [ObservableProperty] private string _feedbackMessage = string.Empty;

    private bool _sessionActive = true;
    private long _loadVersion;
    public bool HasLoadedData { get; private set; }
    public void DeactivateSession()
    {
        _sessionActive = false; HasLoadedData = false;
        EmpresaNombreComercial = EmpresaRazonSocial = EmpresaRfc = EmpresaCodigoPostalFiscal = EmpresaCalleYNumero = "";
        EmpresaColonia = EmpresaCiudadMunicipio = EmpresaEstado = EmpresaTelefonoFijo = EmpresaWhatsappContacto = EmpresaEmailContacto = "";
        FacturamaApiUser = FacturamaApiPassword = EmpresaPasswordKeyCsd = FeedbackMessage = "";
    }
    public EmpresaSettingsViewModel(ISettingsService? settingsService = null, bool loadOnCreate = true)
    {
        _settingsService = settingsService;
        if (loadOnCreate && _settingsService != null) _ = LoadAsync();
    }

    [RelayCommand]
    private void SelectSubTab(string tab)
    {
        IsSubGenerales = tab == "generales";
        IsSubIdentidad = tab == "identidad";
        IsSubFiscal = tab == "fiscal";
        IsSubSucursal = tab == "sucursal";
        IsSubRedes = tab == "redes";
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        HasLoadedData = false;
        if (_settingsService == null) return;
        try
        {
            var appSettings = await _settingsService.GetAppSettingsAsync();
            if (!_sessionActive || version != _loadVersion) return;
            var d = await _settingsService.GetAllAsync();
            if (!_sessionActive || version != _loadVersion) return;
            if (appSettings?.Company != null)
            {
                EmpresaZonaHoraria = appSettings.Company.TimeZoneId;
                EmpresaNombreComercial = appSettings.Company.CommercialName;
                EmpresaRazonSocial = appSettings.Company.LegalName;
                EmpresaRfc = appSettings.Company.Rfc;
                EmpresaRegimenFiscal = appSettings.Company.FiscalRegime;
                EmpresaCodigoPostalFiscal = appSettings.Company.ZipCode;
                EmpresaTelefonoFijo = appSettings.Company.Phone;
                EmpresaEmailContacto = appSettings.Company.Email;
                EmpresaCalleYNumero = appSettings.Company.Address;
                EmpresaSitioWeb = appSettings.Company.Website;
                EmpresaLogoPrincipalUrl = appSettings.Company.LogoPath;
            }

            if (d.TryGetValue("EmpresaNombreComercial", out var nc) && !string.IsNullOrWhiteSpace(nc)) EmpresaNombreComercial = nc;
            else if (string.IsNullOrWhiteSpace(EmpresaNombreComercial) && d.TryGetValue("BusinessName", out var bn)) EmpresaNombreComercial = bn;
            if (d.TryGetValue("EmpresaRazonSocial", out var rs) && !string.IsNullOrWhiteSpace(rs)) EmpresaRazonSocial = rs;
            if (d.TryGetValue("EmpresaGiroComercial", out var gc)) EmpresaGiroComercial = gc;
            if (d.TryGetValue("EmpresaEslogan", out var es)) EmpresaEslogan = es;
            if (d.TryGetValue("EmpresaMonedaPrincipal", out var mp)) EmpresaMonedaPrincipal = mp;
            if (d.TryGetValue("EmpresaRfc", out var rfc) && !string.IsNullOrWhiteSpace(rfc)) EmpresaRfc = rfc;
            if (d.TryGetValue("EmpresaRegimenFiscal", out var rf) && !string.IsNullOrWhiteSpace(rf)) EmpresaRegimenFiscal = rf;
            if (d.TryGetValue("EmpresaCodigoPostalFiscal", out var cp) && !string.IsNullOrWhiteSpace(cp)) EmpresaCodigoPostalFiscal = cp;
            if (d.TryGetValue("EmpresaUsoCfdiPorDefecto", out var uc)) EmpresaUsoCfdiPorDefecto = uc;
            if (d.TryGetValue("EmpresaCertificadoCerPath", out var cer)) EmpresaCertificadoCerPath = cer;
            if (d.TryGetValue("EmpresaCertificadoKeyPath", out var key)) EmpresaCertificadoKeyPath = key;
            if (d.TryGetValue("EmpresaNombreSucursal", out var ns)) EmpresaNombreSucursal = ns;
            if (d.TryGetValue("EmpresaCalleYNumero", out var cn) && !string.IsNullOrWhiteSpace(cn)) EmpresaCalleYNumero = cn;
            else if (string.IsNullOrWhiteSpace(EmpresaCalleYNumero) && d.TryGetValue("BusinessAddress", out var ba)) EmpresaCalleYNumero = ba;
            if (d.TryGetValue("EmpresaColonia", out var col)) EmpresaColonia = col;
            if (d.TryGetValue("EmpresaCiudadMunicipio", out var cm)) EmpresaCiudadMunicipio = cm;
            if (d.TryGetValue("EmpresaEstado", out var est)) EmpresaEstado = est;
            if (d.TryGetValue("EmpresaTelefonoFijo", out var tf) && !string.IsNullOrWhiteSpace(tf)) EmpresaTelefonoFijo = tf;
            else if (string.IsNullOrWhiteSpace(EmpresaTelefonoFijo) && d.TryGetValue("BusinessPhone", out var bp)) EmpresaTelefonoFijo = bp;
            if (d.TryGetValue("EmpresaWhatsappContacto", out var wa)) EmpresaWhatsappContacto = wa;
            if (d.TryGetValue("EmpresaEmailContacto", out var ec) && !string.IsNullOrWhiteSpace(ec)) EmpresaEmailContacto = ec;
            else if (string.IsNullOrWhiteSpace(EmpresaEmailContacto) && d.TryGetValue("BusinessEmail", out var be)) EmpresaEmailContacto = be;
            if (d.TryGetValue("EmpresaSitioWeb", out var web) && !string.IsNullOrWhiteSpace(web)) EmpresaSitioWeb = web;
            if (d.TryGetValue("EmpresaFacebook", out var fb)) EmpresaFacebook = fb;
            if (d.TryGetValue("EmpresaInstagram", out var ig)) EmpresaInstagram = ig;
            if (d.TryGetValue("EmpresaMensajeBienvenidaTicket", out var mb)) EmpresaMensajeBienvenidaTicket = mb;
            if (d.TryGetValue("EmpresaQrRedesUrl", out var qr)) EmpresaQrRedesUrl = qr;
            if (d.TryGetValue("EmpresaLogoPrincipalUrl", out var logo)) EmpresaLogoPrincipalUrl = logo;
            if (d.TryGetValue("EmpresaLogoIsotipoUrl", out var isotipo)) EmpresaLogoIsotipoUrl = isotipo;
            if (d.TryGetValue("EmpresaLogoTermicoUrl", out var thermal)) EmpresaLogoTermicoUrl = thermal;
            if (d.TryGetValue("EmpresaThermalLogoThreshold", out var threshold) && double.TryParse(threshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed) && parsed is >= 0 and <= 255) EmpresaThermalLogoThreshold = parsed;
            if (Ticketfy.Core.Configuration.LocalDevelopmentMode.FacturamaEnabled)
            {
                FacturamaApiUser = _credentialStore.Read("FacturamaApiUser") ?? string.Empty;
                FacturamaApiPassword = _credentialStore.Read("FacturamaApiPassword") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(FacturamaApiUser) && d.TryGetValue("FacturamaApiUser", out var fu) && !string.IsNullOrWhiteSpace(fu))
                {
                    _credentialStore.Write("FacturamaApiUser", fu); FacturamaApiUser = fu;
                    await _settingsService.SetAsync("FacturamaApiUser", string.Empty);
                }
                if (string.IsNullOrWhiteSpace(FacturamaApiPassword) && d.TryGetValue("FacturamaApiPassword", out var fp) && !string.IsNullOrWhiteSpace(fp))
                {
                    _credentialStore.Write("FacturamaApiPassword", fp); FacturamaApiPassword = fp;
                    await _settingsService.SetAsync("FacturamaApiPassword", string.Empty);
                }
                if (d.TryGetValue("FacturamaAmbiente", out var fa)) FacturamaAmbiente = fa;
            }
            HasLoadedData = _sessionActive;
        }
        catch (Exception ex) { Log.Error(ex, "EmpresaSettingsViewModel: error loading"); }
    }

    public async Task<bool> SaveAsync()
    {
        if (_settingsService == null || !_sessionActive || !HasLoadedData) return false;
        try
        {
            var values = new Dictionary<string, string>
            {
                ["EmpresaNombreComercial"] = EmpresaNombreComercial,
                ["EmpresaRazonSocial"] = EmpresaRazonSocial,
                ["EmpresaGiroComercial"] = EmpresaGiroComercial,
                ["EmpresaEslogan"] = EmpresaEslogan,
                ["EmpresaMonedaPrincipal"] = EmpresaMonedaPrincipal,
                ["EmpresaRfc"] = EmpresaRfc,
                ["EmpresaRegimenFiscal"] = EmpresaRegimenFiscal,
                ["EmpresaCodigoPostalFiscal"] = EmpresaCodigoPostalFiscal,
                ["EmpresaUsoCfdiPorDefecto"] = EmpresaUsoCfdiPorDefecto,
                ["EmpresaNombreSucursal"] = EmpresaNombreSucursal,
                ["EmpresaCalleYNumero"] = EmpresaCalleYNumero,
                ["EmpresaColonia"] = EmpresaColonia,
                ["EmpresaCiudadMunicipio"] = EmpresaCiudadMunicipio,
                ["EmpresaEstado"] = EmpresaEstado,
                ["EmpresaTelefonoFijo"] = EmpresaTelefonoFijo,
                ["EmpresaWhatsappContacto"] = EmpresaWhatsappContacto,
                ["EmpresaEmailContacto"] = EmpresaEmailContacto,
                ["EmpresaSitioWeb"] = EmpresaSitioWeb,
                ["EmpresaFacebook"] = EmpresaFacebook,
                ["EmpresaInstagram"] = EmpresaInstagram,
                ["EmpresaMensajeBienvenidaTicket"] = EmpresaMensajeBienvenidaTicket,
                ["EmpresaQrRedesUrl"] = EmpresaQrRedesUrl,
                ["EmpresaLogoPrincipalUrl"] = EmpresaLogoPrincipalUrl,
                ["EmpresaLogoIsotipoUrl"] = EmpresaLogoIsotipoUrl,
                ["EmpresaLogoTermicoUrl"] = EmpresaLogoTermicoUrl,
                ["EmpresaThermalLogoThreshold"] = EmpresaThermalLogoThreshold.ToString(CultureInfo.InvariantCulture)
            };
            if (IsFiscalIntegrationEnabled)
            {
                values["EmpresaCertificadoCerPath"] = EmpresaCertificadoCerPath;
                values["EmpresaCertificadoKeyPath"] = EmpresaCertificadoKeyPath;
            }
            await _settingsService.SaveBusinessSettingsAsync(values);
            if (!_sessionActive) return false;
            if (Ticketfy.Core.Configuration.LocalDevelopmentMode.FacturamaEnabled)
            {
                if (!string.IsNullOrWhiteSpace(FacturamaApiUser)) _credentialStore.Write("FacturamaApiUser", FacturamaApiUser);
                if (!string.IsNullOrWhiteSpace(FacturamaApiPassword)) _credentialStore.Write("FacturamaApiPassword", FacturamaApiPassword);
                await _settingsService.SetAsync("FacturamaApiUser", string.Empty);
                await _settingsService.SetAsync("FacturamaApiPassword", string.Empty);
                await _settingsService.SetAsync("FacturamaAmbiente", FacturamaAmbiente);
            }

            FeedbackMessage = "¡Datos de empresa guardados correctamente!";
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "EmpresaSettingsViewModel: error saving"); if (_sessionActive) FeedbackMessage = ex is InvalidOperationException ? ex.Message : "No se completó el guardado. Revise permisos y datos antes de reintentar."; return false; }
    }

    [RelayCommand] private async Task Save() => await SaveAsync();
}
