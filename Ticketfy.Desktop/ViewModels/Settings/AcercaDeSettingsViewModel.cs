using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Configuration;
using Ticketfy.Services.Implementations;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// ViewModel for Section 6: Acerca de TICKETFY! & Sistema de Actualizaciones OTA.
/// Displays app version, license information, and interactive update check engine.
/// </summary>
public partial class AcercaDeSettingsViewModel : ObservableObject
{
    private readonly AutoUpdateService _autoUpdateService;

    public string AppVersion => AppVersionHelper.DisplayVersion;
    public string FullTitle => AppVersionHelper.FullTitle;
    public string AppDescription => "Ticketfy! Sistema de Gestión de Punto de Venta\nDesarrollado por Studio Kuali / Jóvenes Creadores MX.\nTodas las funciones operan de forma 100% local y offline.";
    public string LicenseType => "Licencia Comercial — Uso exclusivo del titular registrado.";

    [ObservableProperty] private bool _isChecking = false;
    [ObservableProperty] private bool _isUpdateAvailable = false;
    [ObservableProperty] private bool _isUpdateReady = false;
    [ObservableProperty] private bool _isUpToDate = false;
    [ObservableProperty] private bool _isUpdateFailed = false;
    [ObservableProperty] private double _progressPercentage = 0;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public AcercaDeSettingsViewModel()
    {
        _autoUpdateService = new AutoUpdateService();

        _autoUpdateService.UpdateAvailableEvent += () =>
            Dispatcher.UIThread.Post(() =>
            {
                IsChecking = false;
                IsUpdateAvailable = true;
                StatusMessage = "Nueva versión encontrada. Descargando actualización en segundo plano...";
            });

        _autoUpdateService.DownloadProgressChangedEvent += (progress) =>
            Dispatcher.UIThread.Post(() =>
            {
                ProgressPercentage = progress;
                ProgressText = $"{Math.Round(progress)}%";
            });

        _autoUpdateService.UpdateReadyToInstallEvent += () =>
            Dispatcher.UIThread.Post(() =>
            {
                IsChecking = false;
                IsUpdateAvailable = false;
                IsUpdateReady = true;
                StatusMessage = "¡Actualización descargada y lista para instalar!";
            });

        _autoUpdateService.UpdateUpToDateEvent += () =>
            Dispatcher.UIThread.Post(() =>
            {
                IsChecking = false;
                IsUpToDate = true;
                StatusMessage = $"El sistema está actualizado a la última versión ({AppVersion}).";
            });

        _autoUpdateService.UpdateFailedEvent += (errMsg) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsChecking = false;
                IsUpdateFailed = true;
                StatusMessage = $"Error al buscar actualizaciones: {errMsg}";
            });
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsChecking = true;
        IsUpdateAvailable = false;
        IsUpdateReady = false;
        IsUpToDate = false;
        IsUpdateFailed = false;
        ProgressPercentage = 0;
        ProgressText = string.Empty;
        StatusMessage = LocalDevelopmentMode.RemoteInfrastructureUnavailableMessage;

        try
        {
            await _autoUpdateService.CheckAndDownloadUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in AcercaDeSettingsViewModel update check");
            IsChecking = false;
            IsUpdateFailed = true;
            StatusMessage = "No se pudo conectar al servidor de actualizaciones.";
        }
    }

    [RelayCommand]
    private void ApplyUpdateAndRestart()
    {
        _autoUpdateService.ApplyUpdatesAndRestart();
    }
}
