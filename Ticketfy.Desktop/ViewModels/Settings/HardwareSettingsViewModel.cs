using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Ticketfy.Core.Hardware;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Strongly-typed ViewModel for Section 3: Hardware & Tickets POS.
/// Configures ESC/POS thermal printers, paper width (58mm/80mm), barcode scanner and scales.
/// </summary>
public partial class HardwareSettingsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;

    [ObservableProperty] private AppSettings _state = new();
    [ObservableProperty] private string _validationMessage = string.Empty;

    public ObservableCollection<string> PaperWidthOptions { get; } = [
        "80mm (Estándar POS)", "58mm (Térmica Mini)", "Carta / A4 (PDF Fiscal)"
    ];

    private bool _sessionActive = true;
    private long _loadVersion;
    public bool HasLoadedData { get; private set; }
    public void DeactivateSession() { _sessionActive = false; HasLoadedData = false; State = new(); ValidationMessage = ""; }
    public HardwareSettingsViewModel(ISettingsService? settingsService = null, bool loadOnCreate = true)
    {
        _settingsService = settingsService;
        if (loadOnCreate && _settingsService != null) _ = LoadAsync();
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
            HasLoadedData = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in HardwareSettingsViewModel");
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_settingsService == null || !_sessionActive) return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(State.Hardware.PrinterPort) && !HardwareCompatibility.TryValidatePrinterTarget(State.Hardware.PrinterPort, out string error))
            {
                ValidationMessage = error;
                return false;
            }
            if (State.Hardware.CashDrawerPin is not (0 or 1))
            {
                ValidationMessage = "El pin del cajón debe ser 0 (pin 2) o 1 (pin 5).";
                return false;
            }
            await _settingsService.SaveSectionAsync(State, AppSettingsSection.Hardware);
            ValidationMessage = "Configuración guardada. Revise el panel Periféricos; guardar no confirma el funcionamiento físico.";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in HardwareSettingsViewModel");
            ValidationMessage = "No se pudo guardar la configuración. Revise sus permisos y los datos ingresados.";
            return false;
        }
    }
}
