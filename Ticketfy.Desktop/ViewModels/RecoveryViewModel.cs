using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels;

/// <summary>Standalone disaster recovery, reachable before DB bootstrap with --recovery.</summary>
public partial class RecoveryViewModel : ObservableObject
{
    [ObservableProperty] private string _bundlePath = "";
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _newDataDirectory = "";
    [ObservableProperty] private string _message = "Seleccione un paquete de recuperación integral y una carpeta NUEVA. No se abrirá ni reemplazará la base operativa.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _destinationConfirmed;

    [RelayCommand]
    private async Task SelectBundleAsync()
    {
        if (App.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Paquete integral Ticketfy",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Recuperación Ticketfy") { Patterns = ["*.tfyrecovery"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) BundlePath = path;
    }
    partial void OnBundlePathChanged(string value) => DestinationConfirmed = false;
    partial void OnNewDataDirectoryChanged(string value) => DestinationConfirmed = false;

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!DestinationConfirmed) throw new InvalidOperationException("Confirme que la carpeta elegida es nueva y que conservará la instalación original.");
            Message = "Autenticando paquete, verificando bases y auditoría y protegiendo llaves para este usuario Windows…";
            var result = await Task.Run(() => RecoveryBundleService.RestoreToNewDirectoryAsync(BundlePath, Passphrase, NewDataDirectory));
            Message = result.Message + "\nCarpeta: " + result.Directory
                + "\nPara iniciar esta copia, use Ticketfy.Desktop.exe --data-directory seguido de esa ruta entre comillas. Revise caja, saldos y licencia antes de operar.";
            DestinationConfirmed = false;
        }
        catch (Exception ex)
        {
            Message = ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException ? ex.Message
                : "No se pudo completar la recuperación. Revise permisos de carpeta y Windows DPAPI. La instalación original no fue reemplazada.";
        }
        finally { Passphrase = ""; IsBusy = false; }
    }
}
