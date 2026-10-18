using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Data management settings: backup, export CSV, data purge.
/// </summary>
public partial class DatosSettingsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isExporting = false;

    public DatosSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    [RelayCommand]
    private void ExportCsv()
    {
        IsExporting = false;
        FeedbackMessage = "La exportación CSV todavía no está disponible.";
        Log.Warning("CSV export requested, but this feature is not implemented");
    }
}
