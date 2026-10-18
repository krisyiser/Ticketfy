using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Settings;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Unified Reactive Settings ViewModel orchestrating the 4 Key Operational Axes under Protocol Valcore v4.0.
/// Enhanced with UI Customization Sub-Tabs (Tema, Colores, Tipografía, Disposición, Botones, Animaciones)
/// and live component preview based on design skills.
/// </summary>
public partial class UnifiedSettingsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;
    private readonly ThemeEngine _themeEngine = ThemeEngine.Instance;
    private CancellationTokenSource? _saveDebounceCts;

    // 4 Key Operational Axes State
    [ObservableProperty] private bool _isUiTab = true;
    [ObservableProperty] private bool _isCompanyTab = false;
    [ObservableProperty] private bool _isHardwareTab = false;
    [ObservableProperty] private bool _isSystemTab = false;

    // UI Customization Sub-Tabs Navigation State (Eje 1: Personalización & UI)
    [ObservableProperty] private bool _isSubTemaTab = true;
    [ObservableProperty] private bool _isSubColoresTab = false;
    [ObservableProperty] private bool _isSubTipografiaTab = false;
    [ObservableProperty] private bool _isSubDisposicionTab = false;
    [ObservableProperty] private bool _isSubBotonesTab = false;
    [ObservableProperty] private bool _isSubAnimacionesTab = false;

    // Atomic Master AppSettings State POCO
    [ObservableProperty] private AppSettings _state = new();

    // Feedback Message
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    // Color Swatch Palette Collections
    public ObservableCollection<ColorPaletteItem> SuccessColors { get; } = [
        new("Esmeralda Muted", "#10B981"),
        new("Verde POS", "#059669"),
        new("Jade", "#047857"),
        new("Menta Neón", "#34D399")
    ];

    public ObservableCollection<ColorPaletteItem> DangerColors { get; } = [
        new("Rojo Carmesí", "#EF4444"),
        new("Rosa Intenso", "#F43F5E"),
        new("Granate", "#B91C1C"),
        new("Coral Vibrante", "#FB7185")
    ];

    public ObservableCollection<ColorPaletteItem> AccentColors { get; } = [
        new("Azul Eléctrico", "#3B82F6"),
        new("Púrpura", "#8B5CF6"),
        new("Ámbar", "#F59E0B"),
        new("Cian Neón", "#06B6D4"),
        new("Índigo Profundo", "#6366F1"),
        new("Verde Esmeralda", "#10B981")
    ];

    public ObservableCollection<ColorPaletteItem> SidebarColors { get; } = [
        new("Oscuro Profundo", "#090D16"),
        new("Azul Noche", "#0F172A"),
        new("Gris Grafito", "#1E293B"),
        new("Negro OLED", "#000000")
    ];

    public ObservableCollection<string> ThemePresetNames { get; } = [
        "Modo Oscuro", "Modo Claro", "Nordic Slate", "Cyberpunk Dark",
        "Emerald Glass", "Alto Contraste", "Retro Amber"
    ];

    public ObservableCollection<string> AvailableFontOptions { get; } = [
        "Inter", "Roboto", "Montserrat", "Poppins", "JetBrains Mono", "Fira Code",
        "Outfit", "Plus Jakarta Sans", "SF Pro Display", "Open Sans", "Lato", "Consolas"
    ];

    public ObservableCollection<string> PaperWidthOptions { get; } = [
        "80mm (Estándar POS)", "58mm (Térmica Mini)", "Carta / A4 (PDF Fiscal)"
    ];

    public UnifiedSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        if (_settingsService != null) _ = LoadAsync();
    }

    [RelayCommand]
    private void SelectAxisTab(string axis)
    {
        IsUiTab = axis == "ui";
        IsCompanyTab = axis == "company";
        IsHardwareTab = axis == "hardware";
        IsSystemTab = axis == "system";
    }

    [RelayCommand]
    public void SelectSubTab(string subTab)
    {
        IsSubTemaTab = subTab == "tema";
        IsSubColoresTab = subTab == "colores";
        IsSubTipografiaTab = subTab == "tipografia";
        IsSubDisposicionTab = subTab == "disposicion";
        IsSubBotonesTab = subTab == "botones";
        IsSubAnimacionesTab = subTab == "animaciones";
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        State.Visual.ThemeName = themeName;
        OnVisualStateChanged();
    }

    [RelayCommand]
    private void SelectPrimaryColor(string hexColor)
    {
        State.Visual.PrimaryColor = hexColor;
        OnVisualStateChanged();
    }

    [RelayCommand]
    private void SelectSuccessColor(string hexColor)
    {
        State.Visual.SuccessColor = hexColor;
        OnVisualStateChanged();
    }

    [RelayCommand]
    private void SelectDangerColor(string hexColor)
    {
        State.Visual.DangerColor = hexColor;
        OnVisualStateChanged();
    }

    [RelayCommand]
    private void SelectSidebarColor(string hexColor)
    {
        State.Visual.SidebarBgColor = hexColor;
        OnVisualStateChanged();
    }

    [RelayCommand]
    private void SelectDensity(UIDensity density)
    {
        State.Visual.Density = density;
        OnVisualStateChanged();
    }

    /// <summary>
    /// Invoked whenever any visual customization parameter is modified by the user.
    /// Dispatches instant style injection to Avalonia resources and schedules debounced save.
    /// </summary>
    public void OnVisualStateChanged()
    {
        _themeEngine.Apply(State);
        ScheduleDebouncedSave();
    }

    public async Task LoadAsync()
    {
        if (_settingsService == null) return;
        try
        {
            State = await _settingsService.GetAppSettingsAsync();
            _themeEngine.Apply(State);
            Log.Information("UnifiedSettingsViewModel loaded AppSettings successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in UnifiedSettingsViewModel");
        }
    }

    public async Task SaveAsync()
    {
        if (_settingsService == null) return;
        try
        {
            await _settingsService.SaveAppSettingsAsync(State);
            _themeEngine.Apply(State);
            FeedbackMessage = "¡Configuración guardada correctamente!";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in UnifiedSettingsViewModel");
            FeedbackMessage = "Error guardando la configuración.";
        }
    }

    private void ScheduleDebouncedSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        var token = _saveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested && _settingsService != null)
                {
                    await _settingsService.SaveAppSettingsAsync(State);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing debounced settings save");
            }
        }, token);
    }
}
