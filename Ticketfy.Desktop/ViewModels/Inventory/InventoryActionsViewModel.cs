using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Inventory;

/// <summary>
/// Handles inventory CSV imports, snapshots (copia de seguridad), and checklist printing.
/// Extracted from InventoryViewModel.
/// </summary>
public partial class InventoryActionsViewModel : ObservableObject
{
    private readonly IProductService _productService;
    private readonly IExternalCatalogService _externalCatalogService;
    private readonly IInventorySnapshotService? _snapshotService;
    private bool _creatingSnapshot;
    private string _snapshotOperationId = Guid.NewGuid().ToString("N");
    private long _sessionVersion;
    public void ClearSessionData()
    {
        ++_sessionVersion;
        _snapshotOperationId = Guid.NewGuid().ToString("N");
        FeedbackMessage = string.Empty; IsFeedbackError = false;
    }
    private readonly IEscPosPrinterService _printerService;

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;

    public event Action? ProductsUpdated;

    public InventoryActionsViewModel(
        IProductService productService,
        IExternalCatalogService externalCatalogService,
        IInventorySnapshotService? snapshotService = null,
        IEscPosPrinterService? printerService = null)
    {
        _productService = productService;
        _externalCatalogService = externalCatalogService;
        _snapshotService = snapshotService;
        _printerService = printerService ?? new EscPosPrinterService();
    }

    [RelayCommand]
    private async Task ImportProductsFromCsvAsync()
    {
        var version = _sessionVersion;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var storageProvider = desktop.MainWindow.StorageProvider;
                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Seleccionar Archivo CSV de Inventario",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Archivos CSV (*.csv)") { Patterns = new[] { "*.csv" } }
                    }
                });

                if (files.Count > 0 && version == _sessionVersion)
                {
                    var file = files[0];
                    using var stream = await file.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    string content = await Ticketfy.Core.Operations.InventoryWorkloadLimits.ReadCsvAsync(reader);
                    if (version != _sessionVersion) return;

                    int imported = await _productService.ImportFromCsvTextAsync(content);
                    if (version != _sessionVersion) return;
                    IsFeedbackError = false;
                    FeedbackMessage = $"¡Se importaron correctamente {imported} productos!";
                    ProductsUpdated?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InventoryActionsViewModel: error importing CSV");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = ex is InvalidOperationException ? ex.Message : "Error al importar el archivo CSV.";
        }
    }

    public Task ImportCsvCatalogAsync() => ImportProductsFromCsvAsync();

    public async Task PrintChecklistAsync(string? search = null, bool lowStockOnly = false)
    {
        var version = _sessionVersion;
        try
        {
            var products = await _productService.GetInventoryChecklistAsync(search, lowStockOnly);
            if (version != _sessionVersion) return;
            if (products == null || products.Count == 0)
            {
                IsFeedbackError = true;
                FeedbackMessage = "No hay productos registrados en el catálogo para imprimir el checklist.";
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (version != _sessionVersion) return;
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Checklist del filtro actual ({products.Count} productos físicos activos)");
                    var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };
                    var confirmed = await win.ShowDialog<bool>(desktop.MainWindow);
                    if (confirmed && version == _sessionVersion)
                    {
                        // Revalidate permission and scope after a potentially long-lived preview.
                        var printable = await _productService.GetInventoryChecklistAsync(search, lowStockOnly);
                        if (version != _sessionVersion) return;
                        bool printed = await _printerService.PrintInventoryChecklistAsync(printable);
                        if (version != _sessionVersion) return;
                        if (printed)
                        {
                            IsFeedbackError = false;
                            FeedbackMessage = $"Checklist del filtro enviado al controlador ({printable.Count} productos físicos activos). Compruebe el papel.";
                        }
                        else
                        {
                            IsFeedbackError = true;
                            FeedbackMessage = "No se pudo comunicar con la impresora térmica para imprimir el checklist.";
                        }
                    }
                }
                else { IsFeedbackError = true; FeedbackMessage = "No hay ventana disponible para confirmar la impresión. No se envió el checklist."; }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error printing inventory checklist");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = ex is InvalidOperationException ? ex.Message : "Error al generar el checklist de inventario.";
        }
    }

    public async Task CreateSnapshotAsync()
    {
        if (_creatingSnapshot) return;
        var version = _sessionVersion;
        try
        {
            _creatingSnapshot = true;
            var service = _snapshotService ?? throw new InvalidOperationException("No hay servicio de capturas autorizado configurado.");
            // Stable payload as well as operation ID: a lost response must not create a second capture.
            await service.CreateSnapshotAsync("Captura documental solicitada desde inventario", _snapshotOperationId);
            if (version != _sessionVersion) return;
            _snapshotOperationId = Guid.NewGuid().ToString("N");

            IsFeedbackError = false;
            FeedbackMessage = "Captura documental de inventario guardada. No es un respaldo: use Seguridad para recuperación integral.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating inventory snapshot");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = ex is InvalidOperationException ? ex.Message : "No se pudo guardar la captura. Verifique permisos de inventario y costos; puede reintentar sin duplicar una solicitud interrumpida.";
        }
        finally { _creatingSnapshot = false; }
    }

    public async Task ViewSnapshotsAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new InventorySnapshotsViewModel(_snapshotService ?? throw new InvalidOperationException("Servicio de capturas no configurado."), _printerService);
                    var win = new Ticketfy.Views.InventorySnapshotsWindow
                    {
                        DataContext = vm
                    };
                    await win.ShowDialog(desktop.MainWindow);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error viewing inventory snapshots window");
            IsFeedbackError = true;
            FeedbackMessage = "Error al abrir el historial de puntos de guardado.";
        }
    }

    [RelayCommand] public void ClearFeedback() => FeedbackMessage = string.Empty;
}
