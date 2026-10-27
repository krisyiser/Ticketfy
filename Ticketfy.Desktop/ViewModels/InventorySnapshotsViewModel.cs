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
    private readonly Ticketfy.Services.Interfaces.IInventorySnapshotService _snapshotService;
    private bool _assigningDetails;
    private long _detailsVersion;
    private long _listVersion;
    [ObservableProperty] private int _detailPage;
    [ObservableProperty] private bool _hasMoreDetails;
    public string DetailPageLabel => $"Detalle · página {DetailPage + 1} · {SelectedSnapshot?.Items.Count ?? 0} de {SelectedSnapshot?.TotalItems ?? 0} artículos";
    [RelayCommand]
    private async Task PreviousDetailPageAsync()
    { if (DetailPage > 0 && !IsLoading && SelectedSnapshot is not null) { DetailPage--; await LoadSnapshotDetailsAsync(SelectedSnapshot); } }
    [RelayCommand]
    private async Task NextDetailPageAsync()
    { if (HasMoreDetails && !IsLoading && SelectedSnapshot is not null) { DetailPage++; await LoadSnapshotDetailsAsync(SelectedSnapshot); } }
    [ObservableProperty] private int _page;
    [ObservableProperty] private bool _hasMore;
    public string PageLabel => $"Página {Page + 1} · hasta 100 capturas";
    [RelayCommand] private async Task PreviousPageAsync() { if (Page > 0 && !IsLoading) { Page--; await LoadSnapshotsAsync(); } }
    [RelayCommand] private async Task NextPageAsync() { if (HasMore && !IsLoading) { Page++; await LoadSnapshotsAsync(); } }
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
        if (!_assigningDetails)
        {
            ++_detailsVersion; DetailPage = 0; HasMoreDetails = false;
            OnPropertyChanged(nameof(DetailPageLabel));
            if (value != null) _ = LoadSnapshotDetailsAsync(value);
        }
    }

    private async Task LoadSnapshotDetailsAsync(InventorySnapshotEntity target)
    {
        IsLoading = true;
        var version = ++_detailsVersion;
        try
        {
            var details = await _snapshotService.GetSnapshotPageAsync(target.Id, DetailPage);
            if (details != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (version != _detailsVersion || !ReferenceEquals(SelectedSnapshot, target)) return;
                    // ICollection is not observable: replace the selected entity so the detail grid refreshes.
                    _assigningDetails = true;
                    try { SelectedSnapshot = details.Snapshot; HasMoreDetails = details.HasMore; }
                    finally { _assigningDetails = false; }
                    OnPropertyChanged(nameof(DetailPageLabel));
                    StatusMessage = "Valor y cantidad totales corresponden a toda la captura; el detalle y la impresión se consultan por página.";
                });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error loading snapshot details");
            if (version == _detailsVersion)
            {
                SelectedSnapshot = null; HasMoreDetails = false;
                StatusMessage = "No se pudo cargar el detalle autorizado de la captura.";
            }
        }
        finally
        {
            if (version == _detailsVersion || SelectedSnapshot is null) IsLoading = false;
        }
    }

    public InventorySnapshotsViewModel(Ticketfy.Services.Interfaces.IInventorySnapshotService snapshotService, Ticketfy.Services.Interfaces.IEscPosPrinterService? printerService = null)
    {
        _printerService = printerService;
        _snapshotService = snapshotService;
        _ = LoadSnapshotsAsync();
    }

    public async Task LoadSnapshotsAsync()
    {
        var version = ++_listVersion;
        IsLoading = true;
        try
        {
            var list = await _snapshotService.GetSnapshotsAsync(Page);
            if (version != _listVersion) return;
            SelectedSnapshot = null;
            Snapshots = new ObservableCollection<InventorySnapshotEntity>(list);
            HasMore = list.Count == 100;
            OnPropertyChanged(nameof(PageLabel));
            if (Snapshots.Count > 0 && SelectedSnapshot == null)
            {
                _assigningDetails = true;
                try { SelectedSnapshot = Snapshots[0]; }
                finally { _assigningDetails = false; }
                await LoadSnapshotDetailsAsync(SelectedSnapshot);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Inventory snapshot history unavailable");
            if (version != _listVersion) return;
            Snapshots.Clear(); SelectedSnapshot = null; HasMore = false;
            StatusMessage = "No se pudo consultar. Se requieren permisos de inventario y costos.";
        }
        finally
        {
            if (version == _listVersion) IsLoading = false;
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

        string id = SelectedSnapshot.Id;
        int page = DetailPage;
        var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Captura documental: página {page + 1} (hasta 100 artículos)");
        var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };

        var confirmed = await win.ShowDialog<bool>(ownerWin);
        if (!confirmed) return;

        if (_printerService is null) { StatusMessage = "No hay servicio de impresión configurado."; return; }
        // Revalidate authorization and load the complete immutable capture before printing, never a pending/partial list.
        Ticketfy.Services.Interfaces.InventorySnapshotPage? result;
        try { result = await _snapshotService.GetSnapshotPageAsync(id, page); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Snapshot print authorization failed"); StatusMessage = "No se pudo cargar la captura autorizada para impresión."; return; }
        if (result is null) { StatusMessage = "La captura ya no está disponible."; return; }
        var printable = result.Snapshot;
        printable.Notes = $"PAGINA {page + 1}. SOLO {printable.Items.Count} DE {printable.TotalItems} ARTICULOS. TOTALES DE TODA LA CAPTURA.\n" + printable.Notes;
        bool printed;
        try { printed = await _printerService.PrintSnapshotChecklistAsync(printable); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Snapshot printing failed"); StatusMessage = "Falló la impresión. La captura documental permanece guardada."; return; }
        if (printed)
        {
            StatusMessage = $"Página {page + 1} enviada al controlador. Compruebe la salida física; no es un conteo verificado.";
        }
        else
        {
            StatusMessage = "No se pudo comunicar con la impresora térmica.";
        }
    }
}
