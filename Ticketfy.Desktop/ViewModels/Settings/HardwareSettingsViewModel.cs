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

    public HardwareSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        if (_settingsService != null) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_settingsService == null) return;
        try
        {
            State = await _settingsService.GetAppSettingsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in HardwareSettingsViewModel");
        }
    }

    public async Task SaveAsync()
    {
        if (_settingsService == null) return;
        try
        {
            if (!HardwareCompatibility.TryValidatePrinterTarget(State.Hardware.PrinterPort, out string error))
            {
                ValidationMessage = error;
                return;
            }
            if (State.Hardware.CashDrawerPin is not (0 or 1))
            {
                ValidationMessage = "El pin del cajón debe ser 0 (pin 2) o 1 (pin 5).";
                return;
            }
            await _settingsService.SaveAppSettingsAsync(State);
            ValidationMessage = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in HardwareSettingsViewModel");
        }
    }
}
