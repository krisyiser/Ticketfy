using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services;
using System.Collections.ObjectModel;

namespace Ticketfy.ViewModels;

public partial class SettingsViewModel
{
    // ── INTERFAZ SUB-TABS NAVIGATION ────────────────────────────────────────
    [ObservableProperty] private bool _isSubTema = true;
    [ObservableProperty] private bool _isSubColores = false;
    [ObservableProperty] private bool _isSubFuentes = false;
    [ObservableProperty] private bool _isSubDisposicion = false;
    [ObservableProperty] private bool _isSubComponentes = false;
    [ObservableProperty] private bool _isSubAnimaciones = false;

    // ── THEMES & STYLING ───────────────────────────────────────────────────
    [ObservableProperty] private string _selectedTheme = "Midnight Glow (Oscuro)";
    [ObservableProperty] private bool _useGlassmorphism = true;
    [ObservableProperty] private double _glassOpacity = 0.85;
    [ObservableProperty] private double _glassBlurRadius = 20;

    public double GlassmorphismBlur
    {
        get => GlassBlurRadius;
        set => GlassBlurRadius = value;
    }

    public double GlassmorphismOpacity
    {
        get => GlassOpacity;
        set => GlassOpacity = value;
    }

    [ObservableProperty] private string _selectedColorPalette = "Azul Corporativo";
    [ObservableProperty] private string _customPrimaryColorHex = "#2563EB";
    [ObservableProperty] private string _customAccentColorHex = "#3B82F6";

    // ── DISPOSICIÓN & LAYOUT ────────────────────────────────────────────────
    [ObservableProperty] private string _posicionCarrito = "Derecha (Predeterminado)";
    [ObservableProperty] private string _sidebarPosition = "Izquierda (Predeterminado)";
    [ObservableProperty] private string _selectedProductGridColumns = "Autoadaptable (4-6 columnas)";
    [ObservableProperty] private string _modoDensidadEspacial = "Estándar POS (Balanceado)";
    [ObservableProperty] private double _anchoCarritoPx = 380.0;

    public ObservableCollection<string> PosicionCarritoOptions { get; } = ["Derecha (Predeterminado)", "Izquierda"];
    public ObservableCollection<string> PosicionSidebarOptions { get; } = ["Izquierda (Predeterminado)", "Superior (Barra reducida)"];
    public ObservableCollection<string> ProductGridColumnsOptions { get; } = ["Autoadaptable (4-6 columnas)", "3 Columnas (Tarjetas grandes)", "5 Columnas (Compacto)"];
    public ObservableCollection<string> ModoDensidadOptions { get; } = ["Estándar POS (Balanceado)", "Alta Densidad (Modo ERP)", "Teclado Táctil Grande"];

    // ── FUENTES & ESCALA ───────────────────────────────────────────────────
    [ObservableProperty] private string _appFont = "Inter / System Sans-Serif";
    [ObservableProperty] private double _tamanoFuenteBasePx = 13.0;
    [ObservableProperty] private double _tamanoPreciosPosPx = 16.0;

    public ObservableCollection<string> AvailableFontOptions { get; } = [
        "Inter / System Sans-Serif", "Roboto", "Outfit", "Segoe UI", "Consolas / Monospace POS"
    ];

    // ── COLORES EXTRA & ESCALAS ────────────────────────────────────────────
    [ObservableProperty] private double _escalaLogoTopbar = 1.0;
    [ObservableProperty] private double _radioBordesPx = 8.0;
    [ObservableProperty] private bool _showProductImages = true;
    [ObservableProperty] private bool _enableHoverZoom = true;
    [ObservableProperty] private double _duracionTransicionMs = 300.0;
    [ObservableProperty] private bool _enableHoverEffects = true;

    public ObservableCollection<ColorPaletteItem> SuccessColors { get; } = [
        new("Esmeralda Muted", "#059669"), new("Verde POS", "#16A34A"), new("Jade", "#0D9488")
    ];
    public ObservableCollection<ColorPaletteItem> DangerColors { get; } = [
        new("Rojo Carmesí", "#DC2626"), new("Rosa Intenso", "#E11D48"), new("Granate", "#991B1B")
    ];
    public ObservableCollection<ColorPaletteItem> AccentColors { get; } = [
        new("Azul Eléctrico", "#2563EB"), new("Púrpura", "#7C3AED"), new("Ámbar", "#D97706")
    ];
    public ObservableCollection<ColorPaletteItem> SidebarColors { get; } = [
        new("Oscuro Profundo", "#09090B"), new("Azul Noche", "#0F172A"), new("Gris Grafito", "#18181B")
    ];

    [RelayCommand] private void SelectSuccessColor(ColorPaletteItem item) { }
    [RelayCommand] private void SelectDangerColor(ColorPaletteItem item) { }
    [RelayCommand] private void SelectAccent(ColorPaletteItem item) { }
    [RelayCommand] private void SelectSidebarColor(ColorPaletteItem item) { }

    [ObservableProperty] private bool _compactLayoutMode = false;
    [ObservableProperty] private bool _showGridLines = true;
    [ObservableProperty] private bool _roundedCorners = true;
    [ObservableProperty] private double _borderRadiusValue = 8.0;

    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private string _animationSpeed = "Normal (300ms)";

    public ObservableCollection<string> ThemeOptions { get; } = [
        "Midnight Glow (Oscuro)", "Slate Industrial (Gris)", "Pure White (Claro)",
        "Cyberpunk Neon", "Emerald POS", "Dark Amber", "High Contrast (Accesibilidad)"
    ];

    public ObservableCollection<ColorPaletteItem> ColorPalettes { get; } = [
        new("Azul Corporativo", "#2563EB"), new("Esmeralda POS", "#059669"),
        new("Púrpura Neón", "#7C3AED"), new("Naranja Cálido", "#EA580C"),
        new("Rojo Carmesí", "#DC2626"), new("Gris Carbono", "#4B5563")
    ];

    public ObservableCollection<FontSizeScaleOption> FontSizeOptions { get; } = [
        new("Compacto (85%)", "11px", "Mayor densidad de datos"),
        new("Normal (100%)", "13px", "Equilibrio estándar"),
        new("Grande (115%)", "15px", "Mayor legibilidad"),
        new("Extra Grande (130%)", "17px", "Alta accesibilidad")
    ];

    public ObservableCollection<string> AnimationSpeedOptions { get; } = ["Desactivado (0ms)", "Rápido (150ms)", "Normal (300ms)", "Suave (500ms)"];

    [RelayCommand]
    private void SelectInterfazSubTab(string tab)
    {
        IsSubTema = tab == "tema";
        IsSubColores = tab == "colores";
        IsSubFuentes = tab == "fuentes";
        IsSubDisposicion = tab == "disposicion";
        IsSubComponentes = tab == "componentes";
        IsSubAnimaciones = tab == "animaciones";
    }

    [RelayCommand]
    private void SelectTheme(string theme)
    {
        SelectedTheme = theme;
        _themeService.ApplyTheme(theme);
    }
}
