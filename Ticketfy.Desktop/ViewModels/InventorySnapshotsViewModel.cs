using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels;

public partial class InventorySnapshotsViewModel : ObservableObject
{
    private readonly InventorySnapshotService _snapshotService;
    private readonly Ticketfy.Services.Interfaces.IEscPosPrinterService? _printerService;

    [ObservableProperty]
    private ObservableCollection<InventorySnapshotEntity> _snapshots = new();

    [ObservableProperty]
    private InventorySnapshotEntity? _selectedSnapshot;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnSelectedSnapshotChanged(InventorySnapshotEntity? value)
    {
        if (value != null && (value.Items == null || value.Items.Count == 0))
        {
            _ = LoadSnapshotDetailsAsync(value);
        }
    }

    private async Task LoadSnapshotDetailsAsync(InventorySnapshotEntity target)
    {
        IsLoading = true;
        try
        {
            var details = await _snapshotService.GetSnapshotDetailsAsync(target.Id);
            if (details != null && details.Items != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    target.Items.Clear();
                    foreach (var item in details.Items)
                    {
                        target.Items.Add(item);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error loading snapshot details");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public InventorySnapshotsViewModel(Ticketfy.Services.Interfaces.IEscPosPrinterService? printerService = null)
    {
        _printerService = printerService;
        _snapshotService = new InventorySnapshotService();
        _ = LoadSnapshotsAsync();
    }

    private async Task LoadSnapshotsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _snapshotService.GetSnapshotsAsync();
            Snapshots = new ObservableCollection<InventorySnapshotEntity>(list);
            if (Snapshots.Count > 0 && SelectedSnapshot == null)
            {
                SelectedSnapshot = Snapshots[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PrintSnapshotAsync()
    {
        if (SelectedSnapshot == null)
        {
            if (Snapshots.Count > 0)
                SelectedSnapshot = Snapshots[0];
            else
                return;
        }

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow == null) return;

        var ownerWin = System.Linq.Enumerable.FirstOrDefault(desktop.Windows, w => w.IsActive && w.IsVisible) ?? desktop.MainWindow;

        var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Reporte de Captura Física: {SelectedSnapshot.CreatedAt:g}");
        var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };

        var confirmed = await win.ShowDialog<bool>(ownerWin);
        if (!confirmed) return;

        if (_printerService is null) { StatusMessage = "No hay servicio de impresión configurado."; return; }
        bool printed = await _printerService.PrintSnapshotChecklistAsync(SelectedSnapshot);
        if (printed)
        {
            StatusMessage = "¡Reporte de captura física enviado a la impresora térmica con éxito!";
        }
        else
        {
            StatusMessage = "No se pudo comunicar con la impresora térmica.";
        }
    }
}
