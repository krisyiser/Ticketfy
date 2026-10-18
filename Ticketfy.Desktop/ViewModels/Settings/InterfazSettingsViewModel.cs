using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Manages theme, colors, typography, layout density, component geometry, and animations.
/// Encapsulates theme configuration and communicates with ThemeService.
/// Re-architected under Protocol Valcore v4.0 for instant real-time reactive theme mutation.
/// </summary>
public partial class InterfazSettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService = ThemeService.Instance;
    private readonly ISettingsService? _settingsService;

    // Sub-tab selection inside Interfaz
    [ObservableProperty] private bool _isSubTema = true;
    [ObservableProperty] private bool _isSubColores = false;
    [ObservableProperty] private bool _isSubFuentes = false;
    [ObservableProperty] private bool _isSubDisposicion = false;
    [ObservableProperty] private bool _isSubComponentes = false;
    [ObservableProperty] private bool _isSubAnimaciones = false;

    // Theme & Background
    [ObservableProperty] private string _currentTheme = "Modo Claro";
    [ObservableProperty] private bool _modoHorarioAuto = false;
    [ObservableProperty] private string _estiloFondo = "Sólido";
    [ObservableProperty] private double _glassmorphismBlur = 0.0;
    [ObservableProperty] private double _glassmorphismOpacity = 100.0;
    [ObservableProperty] private double _borderGlowIntensity = 1.0;

    public ObservableCollection<string> ThemePresetNames { get; } = [
        "Modo Claro", "Modo Oscuro", "Alto Contraste", "Cyberpunk Dark",
        "Emerald Glass", "Nordic Slate", "Retro Amber"
    ];

    public ObservableCollection<string> EstiloFondoOptions { get; } = [
        "Sólido", "Gradiente Diagonal", "Malla de Puntos (Dot Matrix)", "Líneas Diagonales", "Ruido Sutil"
    ];

    // Colors & Identity
    [ObservableProperty] private string _accentColor = "#3B82F6";
    [ObservableProperty] private string _colorSecundario = "#38BDF8";
    [ObservableProperty] private string _colorExitoCobro = "#10B981";
    [ObservableProperty] private string _colorAlertaCancelacion = "#EF4444";
    [ObservableProperty] private string _sidebarBgColor = "#0F172A";
    [ObservableProperty] private string _colorFondoContenedores = "#1E293B";
    [ObservableProperty] private string _colorTextoPrincipal = "#F8FAFC";
    [ObservableProperty] private string _colorTextoSecundario = "#94A3B8";

    [ObservableProperty] private bool _mostrarLogoEnTopbar = true;
    [ObservableProperty] private double _escalaLogoTopbar = 32.0;
    [ObservableProperty] private bool _invertirLogoModoOscuro = false;

    public ObservableCollection<ColorPaletteItem> SuccessColors { get; } = [
        new("Verde Esmeralda", "#10B981"),
        new("Verde Bosque", "#059669"),
        new("Verde Neón", "#22C55E"),
        new("Verde Oliva", "#65A30D")
    ];

    public ObservableCollection<ColorPaletteItem> DangerColors { get; } = [
        new("Rojo Coral", "#EF4444"),
        new("Rojo Rubí", "#DC2626"),
        new("Rojo Carmesí", "#E11D48"),
        new("Naranja Alerta", "#F97316")
    ];

    public ObservableCollection<ColorPaletteItem> AccentColors { get; } = [
        new("Azul Zafiro", "#3B82F6"),
        new("Azul Índigo", "#6366F1"),
        new("Púrpura Neón", "#8B5CF6"),
        new("Cian Eléctrico", "#06B6D4"),
        new("Teal Esmeralda", "#14B8A6"),
        new("Ámbar Dorado", "#F59E0B"),
        new("Rosa Magenta", "#EC4899"),
        new("Gris Acero", "#64748B")
    ];

    public ObservableCollection<ColorPaletteItem> SidebarColors { get; } = [
        new("Slate Industrial", "#0F172A"),
        new("Negro Medianoche", "#09090B"),
        new("Azul Marino", "#1E3A8A"),
        new("Verde Oscuro", "#022C22")
    ];

    // Typography
    [ObservableProperty] private string _appFont = "Inter";
    [ObservableProperty] private double _tamanoFuenteBasePx = 14.0;
    [ObservableProperty] private double _tamanoPreciosPosPx = 24.0;
    [ObservableProperty] private double _espaciadoLetrasPx = 0.0;
    [ObservableProperty] private string _grosorTitulos = "Bold (700)";
    [ObservableProperty] private string _grosorNumerosPrecios = "Bold (700)";

    public ObservableCollection<string> AvailableFontOptions { get; } = [
        "Inter", "Roboto", "Montserrat", "Poppins", "JetBrains Mono", "Fira Code",
        "Outfit", "Plus Jakarta Sans", "SF Pro Display", "Open Sans", "Lato", "Consolas"
    ];

    // Layout
    [ObservableProperty] private string _posicionCarrito = "Derecha";
    [ObservableProperty] private string _sidebarPosition = "Izquierda";
    [ObservableProperty] private double _anchoCarritoPx = 380.0;
    [ObservableProperty] private string _selectedProductGridColumns = "4 Columnas";
    [ObservableProperty] private double _gridGapPx = 12.0;
    [ObservableProperty] private string _modoDensidadEspacial = "Estándar";

    public ObservableCollection<string> PosicionCarritoOptions { get; } = ["Derecha", "Izquierda"];
    public ObservableCollection<string> PosicionSidebarOptions { get; } = ["Izquierda", "Derecha", "Arriba (Top Bar)", "Abajo (Bottom Bar)"];
    public ObservableCollection<string> ProductGridColumnsOptions { get; } = ["3 Columnas", "4 Columnas", "5 Columnas", "6 Columnas", "8 Columnas", "Lista de Alta Densidad"];
    public ObservableCollection<string> ModoDensidadOptions { get; } = ["Cómodo / Touch", "Estándar", "Compacto / Alta Densidad"];

    // Components & Geometry
    [ObservableProperty] private double _radioBordesPx = 6.0;
    [ObservableProperty] private string _estiloSombras = "Elevación Sutil";
    [ObservableProperty] private double _grosorBordePx = 1.0;
    [ObservableProperty] private string _setDeIconos = "Lucide (Línea Fina)";

    [ObservableProperty] private bool _showProductImages = true;
    [ObservableProperty] private bool _showSkuProducto = true;
    [ObservableProperty] private bool _showStockBadge = true;
    [ObservableProperty] private bool _showCategoriaBadge = false;
    [ObservableProperty] private bool _showQuickAddButton = true;
    [ObservableProperty] private bool _enableHoverZoom = true;

    // Animations
    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private bool _enableHoverEffects = true;
    [ObservableProperty] private bool _enableActiveScaleDown = true;
    [ObservableProperty] private double _duracionTransicionMs = 120.0;
    [ObservableProperty] private string _selectedEasingFunction = "CubicEaseOut";
    [ObservableProperty] private string _tipoEntradaModulos = "Slide Horizontal";

    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public InterfazSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        if (_settingsService != null) _ = LoadAsync();
    }

    [RelayCommand]
    private void SelectSubTab(string tab)
    {
        IsSubTema = tab == "tema";
        IsSubColores = tab == "colores";
        IsSubFuentes = tab == "fuentes";
        IsSubDisposicion = tab == "disposicion";
        IsSubComponentes = tab == "componentes";
        IsSubAnimaciones = tab == "animaciones";
    }

    [RelayCommand] private void SelectTheme(string themeName) => CurrentTheme = themeName;
    [RelayCommand] private void SelectAccent(string hexColor) => AccentColor = hexColor;
    [RelayCommand] private void SelectSuccessColor(string hexColor) => ColorExitoCobro = hexColor;
    [RelayCommand] private void SelectDangerColor(string hexColor) => ColorAlertaCancelacion = hexColor;
    [RelayCommand] private void SelectSidebarColor(string hexColor) => SidebarBgColor = hexColor;

    // ═══ REAL-TIME INSTANT REACTIVE RE-RENDERING HOOKS ═══
    partial void OnCurrentThemeChanged(string value) => _themeService.ApplyTheme(value);
    partial void OnAccentColorChanged(string value) => _themeService.ApplyAccentColor(value);
    partial void OnColorExitoCobroChanged(string value) => _themeService.ApplySuccessColor(value);
    partial void OnColorAlertaCancelacionChanged(string value) => _themeService.ApplyDangerColor(value);
    partial void OnSidebarBgColorChanged(string value) => _themeService.ApplySidebarColor(value);
    partial void OnAppFontChanged(string value) => _themeService.ApplyFont(value);
    partial void OnTamanoFuenteBasePxChanged(double value) => _themeService.ApplyBaseFontSize(value);
    partial void OnTamanoPreciosPosPxChanged(double value) => _themeService.ApplyPosPriceFontSize(value);
    partial void OnAnchoCarritoPxChanged(double value) => _themeService.ApplyCartWidth(value);
    partial void OnRadioBordesPxChanged(double value) => _themeService.ApplyBorderRadius(value);
    partial void OnGlassmorphismBlurChanged(double value) => _themeService.ApplyGlassmorphismBlur(value);
    partial void OnGlassmorphismOpacityChanged(double value) => _themeService.ApplyGlassmorphismOpacity(value);
    partial void OnDuracionTransicionMsChanged(double value) => _themeService.ApplyTransitionDuration(value);
    partial void OnSidebarPositionChanged(string value) => _themeService.ApplySidebarPosition(value);
    partial void OnPosicionCarritoChanged(string value) => _themeService.ApplyCartPosition(value);
    partial void OnGrosorBordePxChanged(double value) => _themeService.ApplyBorderWidth(value);
    partial void OnEscalaLogoTopbarChanged(double value) => _themeService.ApplyLogoScale(value);

    public async Task LoadAsync()
    {
        if (_settingsService == null) return;
        try
        {
            var d = await _settingsService.GetAllAsync();
            if (d.TryGetValue("CurrentTheme", out var ct)) CurrentTheme = ct;
            if (d.TryGetValue("AccentColor", out var ac)) AccentColor = ac;
            if (d.TryGetValue("AppFont", out var af)) AppFont = af;
            if (d.TryGetValue("SidebarPosition", out var sp)) SidebarPosition = sp;
            if (d.TryGetValue("RadioBordesPx", out var rb) && double.TryParse(rb, out var rbVal)) RadioBordesPx = rbVal;
            if (d.TryGetValue("TamanoFuenteBasePx", out var tf) && double.TryParse(tf, out var tfVal)) TamanoFuenteBasePx = tfVal;
            if (d.TryGetValue("TamanoPreciosPosPx", out var tp) && double.TryParse(tp, out var tpVal)) TamanoPreciosPosPx = tpVal;
            if (d.TryGetValue("AnchoCarritoPx", out var acw) && double.TryParse(acw, out var acwVal)) AnchoCarritoPx = acwVal;
            if (d.TryGetValue("GlassmorphismBlur", out var gb) && double.TryParse(gb, out var gbVal)) GlassmorphismBlur = gbVal;
            if (d.TryGetValue("GlassmorphismOpacity", out var go) && double.TryParse(go, out var goVal)) GlassmorphismOpacity = goVal;
        }
        catch (Exception ex) { Log.Error(ex, "InterfazSettingsViewModel: error loading"); }
    }

    public async Task SaveAsync()
    {
        if (_settingsService == null) return;
        try
        {
            await _settingsService.SetAsync("CurrentTheme", CurrentTheme);
            await _settingsService.SetAsync("AccentColor", AccentColor);
            await _settingsService.SetAsync("AppFont", AppFont);
            await _settingsService.SetAsync("SidebarPosition", SidebarPosition);
            await _settingsService.SetAsync("RadioBordesPx", RadioBordesPx.ToString());
            await _settingsService.SetAsync("TamanoFuenteBasePx", TamanoFuenteBasePx.ToString());
            await _settingsService.SetAsync("TamanoPreciosPosPx", TamanoPreciosPosPx.ToString());
            await _settingsService.SetAsync("AnchoCarritoPx", AnchoCarritoPx.ToString());
            await _settingsService.SetAsync("GlassmorphismBlur", GlassmorphismBlur.ToString());
            await _settingsService.SetAsync("GlassmorphismOpacity", GlassmorphismOpacity.ToString());

            _themeService.ApplyTheme(CurrentTheme);
            _themeService.ApplyAccentColor(AccentColor);
            _themeService.ApplySidebarPosition(SidebarPosition);
            _themeService.ApplyBorderRadius(RadioBordesPx);
            _themeService.ApplyBaseFontSize(TamanoFuenteBasePx);
            _themeService.ApplyPosPriceFontSize(TamanoPreciosPosPx);
            _themeService.ApplyCartWidth(AnchoCarritoPx);

            FeedbackMessage = "¡Ajustes de interfaz guardados correctamente!";
        }
        catch (Exception ex) { Log.Error(ex, "InterfazSettingsViewModel: error saving"); }
    }

    [RelayCommand] private async Task Save() => await SaveAsync();
}
