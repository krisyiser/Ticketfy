using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Shell;

/// <summary>
/// Manages OTA auto-update state and user-facing feedback banners.
/// Decoupled from MainWindowViewModel to enforce single-responsibility.
/// </summary>
public partial class AutoUpdateViewModel : ObservableObject
{
    private readonly Ticketfy.Services.Implementations.AutoUpdateService _autoUpdateService;

    [ObservableProperty] private bool _isUpdateAvailable = false;
    [ObservableProperty] private bool _isUpdateReady = false;
    [ObservableProperty] private double _updateProgress = 0;
    [ObservableProperty] private string _updateProgressText = string.Empty;
    [ObservableProperty] private bool _isUpdateUpToDate = false;
    [ObservableProperty] private bool _isUpdateFailed = false;
    [ObservableProperty] private string _updateErrorMessage = string.Empty;

    public AutoUpdateViewModel()
    {
        _autoUpdateService = new Ticketfy.Services.Implementations.AutoUpdateService();

        _autoUpdateService.UpdateAvailableEvent += () =>
            Dispatcher.UIThread.Post(() => IsUpdateAvailable = true);

        _autoUpdateService.DownloadProgressChangedEvent += (progress) =>
            Dispatcher.UIThread.Post(() =>
            {
                UpdateProgress = progress;
                UpdateProgressText = progress > 0 ? $"{Math.Round(progress)}%" : string.Empty;
            });

        _autoUpdateService.UpdateReadyToInstallEvent += () =>
            Dispatcher.UIThread.Post(() =>
            {
                IsUpdateAvailable = false;
                IsUpdateReady = true;
            });

        _autoUpdateService.UpdateFailedEvent += (msg) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsUpdateAvailable = false;
                IsUpdateFailed = true;
                UpdateErrorMessage = $"Fallo al buscar actualizaciones: {msg}";
                Task.Delay(5000).ContinueWith(_ =>
                    Dispatcher.UIThread.Post(() => IsUpdateFailed = false));
            });

        _autoUpdateService.UpdateUpToDateEvent += () =>
            Dispatcher.UIThread.Post(() =>
            {
                IsUpdateUpToDate = true;
                Task.Delay(3000).ContinueWith(_ =>
                    Dispatcher.UIThread.Post(() => IsUpdateUpToDate = false));
            });

    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        IsUpdateFailed = false;
        IsUpdateUpToDate = false;
        await _autoUpdateService.CheckAndDownloadUpdatesAsync();
    }

    [RelayCommand]
    private void ApplyUpdateAndRestart()
    {
        _autoUpdateService.ApplyUpdatesAndRestart();
    }
}
