using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Services.Security;
using System;
using System.Collections.ObjectModel;
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
    [ObservableProperty] private string _csdValidityStatus = "VÁLIDO";
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
    [ObservableProperty] private bool _isSubGenerales = true;
    [ObservableProperty] private bool _isSubIdentidad = false;
    [ObservableProperty] private bool _isSubFiscal = false;
    [ObservableProperty] private bool _isSubSucursal = false;
    [ObservableProperty] private bool _isSubRedes = false;

    public bool IsSubEmpresaGenerales => IsSubGenerales;
    public bool IsSubEmpresaIdentidad => IsSubIdentidad;
    public bool IsSubEmpresaFiscal => IsSubFiscal;
    public bool IsSubEmpresaSucursal => IsSubSucursal;
    public bool IsSubEmpresaRedes => IsSubRedes;

    [RelayCommand] private void SelectEmpresaSubTab(string tab) => SelectSubTab(tab);

    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public EmpresaSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        if (_settingsService != null) _ = LoadAsync();
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
        if (_settingsService == null) return;
        try
        {
            var appSettings = await _settingsService.GetAppSettingsAsync();
            if (appSettings?.Company != null)
            {
                if (!string.IsNullOrWhiteSpace(appSettings.Company.CommercialName)) EmpresaNombreComercial = appSettings.Company.CommercialName;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.LegalName)) EmpresaRazonSocial = appSettings.Company.LegalName;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.Rfc)) EmpresaRfc = appSettings.Company.Rfc;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.FiscalRegime)) EmpresaRegimenFiscal = appSettings.Company.FiscalRegime;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.ZipCode)) EmpresaCodigoPostalFiscal = appSettings.Company.ZipCode;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.Phone)) EmpresaTelefonoFijo = appSettings.Company.Phone;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.Email)) EmpresaEmailContacto = appSettings.Company.Email;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.Address)) EmpresaCalleYNumero = appSettings.Company.Address;
                if (!string.IsNullOrWhiteSpace(appSettings.Company.Website)) EmpresaSitioWeb = appSettings.Company.Website;
            }

            var d = await _settingsService.GetAllAsync();
            if (d.TryGetValue("EmpresaNombreComercial", out var nc) && !string.IsNullOrWhiteSpace(nc)) EmpresaNombreComercial = nc;
            else if (string.IsNullOrWhiteSpace(EmpresaNombreComercial) && d.TryGetValue("BusinessName", out var bn)) EmpresaNombreComercial = bn;
            if (d.TryGetValue("EmpresaRazonSocial", out var rs) && !string.IsNullOrWhiteSpace(rs)) EmpresaRazonSocial = rs;
            if (d.TryGetValue("EmpresaGiroComercial", out var gc)) EmpresaGiroComercial = gc;
            if (d.TryGetValue("EmpresaEslogan", out var es)) EmpresaEslogan = es;
            if (d.TryGetValue("EmpresaMonedaPrincipal", out var mp)) EmpresaMonedaPrincipal = mp;
            if (d.TryGetValue("EmpresaZonaHoraria", out var zh)) EmpresaZonaHoraria = zh;
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
            if (d.TryGetValue("EmpresaEstado", out var est) && est != "Ciudad de México") EmpresaEstado = est;
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
        catch (Exception ex) { Log.Error(ex, "EmpresaSettingsViewModel: error loading"); }
    }

    public async Task SaveAsync()
    {
        if (_settingsService == null) return;
        try
        {
            await _settingsService.SetAsync("EmpresaNombreComercial", EmpresaNombreComercial);
            await _settingsService.SetAsync("BusinessName", EmpresaNombreComercial);
            await _settingsService.SetAsync("EmpresaRazonSocial", EmpresaRazonSocial);
            await _settingsService.SetAsync("EmpresaGiroComercial", EmpresaGiroComercial);
            await _settingsService.SetAsync("EmpresaEslogan", EmpresaEslogan);
            await _settingsService.SetAsync("EmpresaMonedaPrincipal", EmpresaMonedaPrincipal);
            await _settingsService.SetAsync("EmpresaZonaHoraria", EmpresaZonaHoraria);
            await _settingsService.SetAsync("EmpresaRfc", EmpresaRfc);
            await _settingsService.SetAsync("EmpresaRegimenFiscal", EmpresaRegimenFiscal);
            await _settingsService.SetAsync("EmpresaCodigoPostalFiscal", EmpresaCodigoPostalFiscal);
            await _settingsService.SetAsync("EmpresaUsoCfdiPorDefecto", EmpresaUsoCfdiPorDefecto);
            await _settingsService.SetAsync("EmpresaCertificadoCerPath", EmpresaCertificadoCerPath);
            await _settingsService.SetAsync("EmpresaCertificadoKeyPath", EmpresaCertificadoKeyPath);
            await _settingsService.SetAsync("EmpresaNombreSucursal", EmpresaNombreSucursal);
            await _settingsService.SetAsync("EmpresaCalleYNumero", EmpresaCalleYNumero);
            await _settingsService.SetAsync("BusinessAddress", EmpresaCalleYNumero);
            await _settingsService.SetAsync("EmpresaColonia", EmpresaColonia);
            await _settingsService.SetAsync("EmpresaCiudadMunicipio", EmpresaCiudadMunicipio);
            await _settingsService.SetAsync("EmpresaEstado", EmpresaEstado);
            await _settingsService.SetAsync("EmpresaTelefonoFijo", EmpresaTelefonoFijo);
            await _settingsService.SetAsync("BusinessPhone", EmpresaTelefonoFijo);
            await _settingsService.SetAsync("EmpresaWhatsappContacto", EmpresaWhatsappContacto);
            await _settingsService.SetAsync("EmpresaEmailContacto", EmpresaEmailContacto);
            await _settingsService.SetAsync("BusinessEmail", EmpresaEmailContacto);
            await _settingsService.SetAsync("EmpresaSitioWeb", EmpresaSitioWeb);
            await _settingsService.SetAsync("EmpresaFacebook", EmpresaFacebook);
            await _settingsService.SetAsync("EmpresaInstagram", EmpresaInstagram);
            await _settingsService.SetAsync("EmpresaMensajeBienvenidaTicket", EmpresaMensajeBienvenidaTicket);
            await _settingsService.SetAsync("EmpresaQrRedesUrl", EmpresaQrRedesUrl);
            if (!string.IsNullOrWhiteSpace(FacturamaApiUser)) _credentialStore.Write("FacturamaApiUser", FacturamaApiUser);
            if (!string.IsNullOrWhiteSpace(FacturamaApiPassword)) _credentialStore.Write("FacturamaApiPassword", FacturamaApiPassword);
            await _settingsService.SetAsync("FacturamaApiUser", string.Empty);
            await _settingsService.SetAsync("FacturamaApiPassword", string.Empty);
            await _settingsService.SetAsync("FacturamaAmbiente", FacturamaAmbiente);

            FeedbackMessage = "¡Datos de empresa guardados correctamente!";
        }
        catch (Exception ex) { Log.Error(ex, "EmpresaSettingsViewModel: error saving"); }
    }

    [RelayCommand] private async Task Save() => await SaveAsync();
}
