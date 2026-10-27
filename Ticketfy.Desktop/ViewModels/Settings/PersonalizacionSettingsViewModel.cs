using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Settings;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Strongly-typed ViewModel for Section 1: Personalización & UI.
/// Reactive theme engine dispatcher with real-time 0ms POS component preview.
/// Listen to VisualCustomizationConfig PropertyChanged events to instantly apply and save settings.
/// </summary>
public partial class PersonalizacionSettingsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;
    private readonly ThemeEngine _themeEngine = ThemeEngine.Instance;
    private CancellationTokenSource? _saveDebounceCts;
    private bool _sessionActive = true;
    private long _loadVersion;
    public bool HasLoadedData { get; private set; }
    public void DeactivateSession()
    {
        _sessionActive = false;
        HasLoadedData = false;
        _saveDebounceCts?.Cancel();
        if (State?.Visual is not null) State.Visual.PropertyChanged -= Visual_PropertyChanged;
    }

    // Sub-Tabs Navigation State inside Personalización
    [ObservableProperty] private bool _isSubTemaTab = true;
    [ObservableProperty] private bool _isSubColoresTab = false;
    [ObservableProperty] private bool _isSubTipografiaTab = false;
    [ObservableProperty] private bool _isSubDisposicionTab = false;
    [ObservableProperty] private bool _isSubBotonesTab = false;
    [ObservableProperty] private bool _isSubAnimacionesTab = false;

    // Master AppSettings POCO
    [ObservableProperty] private AppSettings _state = new();

    partial void OnStateChanged(AppSettings value)
    {
        if (value?.Visual != null)
        {
            value.Visual.PropertyChanged -= Visual_PropertyChanged;
            value.Visual.PropertyChanged += Visual_PropertyChanged;
        }
    }

    private void Visual_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnVisualStateChanged();
    }

    // Universal Master 12-Color Palette (12 Distinct Chromatic Hues)
    public ObservableCollection<ColorPaletteItem> Master12ColorPalette { get; } = [
        new("Azul Eléctrico", "#3B82F6"),
        new("Verde Esmeralda", "#10B981"),
        new("Rojo Carmesí", "#EF4444"),
        new("Púrpura Neón", "#8B5CF6"),
        new("Ámbar Dorado", "#F59E0B"),
        new("Cian Ártico", "#06B6D4"),
        new("Rosa Magenta", "#EC4899"),
        new("Naranja Neón", "#F97316"),
        new("Lima Verde", "#84CC16"),
        new("Índigo Violeta", "#6366F1"),
        new("Gris Grafito", "#475569"),
        new("Negro OLED", "#090D16")
    ];

    public ObservableCollection<string> AvailableFontOptions { get; } = [
        "Inter", "Roboto", "Montserrat", "Poppins", "JetBrains Mono", "Fira Code",
        "Outfit", "Plus Jakarta Sans", "SF Pro Display", "Open Sans", "Lato", "Consolas"
    ];

    public PersonalizacionSettingsViewModel(ISettingsService? settingsService = null, bool loadOnCreate = true)
    {
        _settingsService = settingsService;
        if (State?.Visual != null)
        {
            State.Visual.PropertyChanged += Visual_PropertyChanged;
        }
        if (loadOnCreate && _settingsService != null) _ = LoadAsync();
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
        switch (themeName)
        {
            case "Modo Claro":
            case "Light":
                State.Visual.Mode = ThemeMode.Light;
                State.Visual.PrimaryColor = "#0284C7";
                State.Visual.SuccessColor = "#059669";
                State.Visual.DangerColor = "#DC2626";
                State.Visual.SidebarBgColor = "#F1F5F9";
                State.Visual.CornerRadius = 6.0;
                State.Visual.FontFamily = "Outfit";
                break;

            case "Alto Contraste":
                State.Visual.Mode = ThemeMode.HighContrast;
                State.Visual.PrimaryColor = "#FACC15";
                State.Visual.SuccessColor = "#00FF66";
                State.Visual.DangerColor = "#FF0033";
                State.Visual.SidebarBgColor = "#000000";
                State.Visual.CornerRadius = 0.0;
                State.Visual.FontFamily = "Roboto";
                break;

            case "Cyberpunk Dark":
                State.Visual.Mode = ThemeMode.Cyberpunk;
                State.Visual.PrimaryColor = "#EC4899";
                State.Visual.SuccessColor = "#06B6D4";
                State.Visual.DangerColor = "#FF2E63";
                State.Visual.SidebarBgColor = "#0B0719";
                State.Visual.CornerRadius = 4.0;
                State.Visual.FontFamily = "JetBrains Mono";
                break;

            case "Emerald Glass":
                State.Visual.Mode = ThemeMode.Emerald;
                State.Visual.PrimaryColor = "#10B981";
                State.Visual.SuccessColor = "#34D399";
                State.Visual.DangerColor = "#F43F5E";
                State.Visual.SidebarBgColor = "#011711";
                State.Visual.CornerRadius = 10.0;
                State.Visual.FontFamily = "Montserrat";
                break;

            case "Nordic Slate":
                State.Visual.Mode = ThemeMode.Nordic;
                State.Visual.PrimaryColor = "#38BDF8";
                State.Visual.SuccessColor = "#34D399";
                State.Visual.DangerColor = "#F87171";
                State.Visual.SidebarBgColor = "#0F172A";
                State.Visual.CornerRadius = 12.0;
                State.Visual.FontFamily = "Plus Jakarta Sans";
                break;

            case "Modo Oscuro":
            default:
                State.Visual.Mode = ThemeMode.Dark;
                State.Visual.PrimaryColor = "#3B82F6";
                State.Visual.SuccessColor = "#10B981";
                State.Visual.DangerColor = "#EF4444";
                State.Visual.SidebarBgColor = "#0B111E";
                State.Visual.CornerRadius = 8.0;
                State.Visual.FontFamily = "Inter";
                break;
        }
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

    public void OnVisualStateChanged()
    {
        if (!_sessionActive) return;
        _themeEngine.Apply(State);
        ScheduleDebouncedSave();
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        HasLoadedData = false;
        if (_settingsService == null) return;
        try
        {
            var loaded = await _settingsService.GetAppSettingsAsync();
            if (!_sessionActive || version != _loadVersion) return;
            State = loaded;
            if (State?.Visual != null)
            {
                State.Visual.PropertyChanged -= Visual_PropertyChanged;
                State.Visual.PropertyChanged += Visual_PropertyChanged;
            }
            if (State != null) _themeEngine.Apply(State);
            HasLoadedData = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in PersonalizacionSettingsViewModel");
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_settingsService == null || !_sessionActive) return false;
        try
        {
            await _settingsService.SaveSectionAsync(State, AppSettingsSection.Visual);
            if (!_sessionActive) return false;
            _themeEngine.Apply(State);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in PersonalizacionSettingsViewModel");
            return false;
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
                    await _settingsService.SaveSectionAsync(State, AppSettingsSection.Visual);
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
