using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Implementations;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class SettingsViewModel
{
    // ── EMPRESA GENERALES ──────────────────────────────────────────────────
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

    // ── EMPRESA IDENTIDAD ──────────────────────────────────────────────────
    [ObservableProperty] private string _empresaLogoPrincipalUrl = "";
    [ObservableProperty] private string _empresaLogoIsotipoUrl = "";
    [ObservableProperty] private string _empresaLogoTermicoUrl = "";
    [ObservableProperty] private string _empresaColorCorporativoHex = "#2563EB";
    [ObservableProperty] private bool _empresaSincronizarColorSistema = true;
    [ObservableProperty] private double _empresaThermalLogoThreshold = 128.0;

    // ── EMPRESA FISCAL ─────────────────────────────────────────────────────
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

    // ── EMPRESA SUCURSAL ───────────────────────────────────────────────────
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

    // ── EMPRESA REDES ──────────────────────────────────────────────────────
    [ObservableProperty] private string _empresaEmailContacto = string.Empty;
    [ObservableProperty] private string _empresaSitioWeb = string.Empty;
    [ObservableProperty] private string _empresaFacebook = string.Empty;
    [ObservableProperty] private string _empresaInstagram = string.Empty;
    [ObservableProperty] private string _empresaTiktok = string.Empty;
    [ObservableProperty] private string _empresaMensajeBienvenidaTicket = string.Empty;
    [ObservableProperty] private string _empresaQrRedesUrl = string.Empty;

    // ── EMPRESA SUB-TABS NAVIGATION ─────────────────────────────────────────
    [ObservableProperty] private bool _isSubEmpresaGenerales = true;
    [ObservableProperty] private bool _isSubEmpresaIdentidad = false;
    [ObservableProperty] private bool _isSubEmpresaFiscal = false;
    [ObservableProperty] private bool _isSubEmpresaSucursal = false;
    [ObservableProperty] private bool _isSubEmpresaRedes = false;

    [RelayCommand]
    private void SelectEmpresaSubTab(string tab)
    {
        IsSubEmpresaGenerales = tab == "generales";
        IsSubEmpresaIdentidad = tab == "identidad";
        IsSubEmpresaFiscal = tab == "fiscal";
        IsSubEmpresaSucursal = tab == "sucursal";
        IsSubEmpresaRedes = tab == "redes";
    }
}
