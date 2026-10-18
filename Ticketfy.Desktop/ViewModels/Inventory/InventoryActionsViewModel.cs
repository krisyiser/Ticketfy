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
    private readonly IInventorySnapshotService _snapshotService;
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
        _snapshotService = snapshotService ?? new InventorySnapshotService();
        _printerService = printerService ?? new EscPosPrinterService();
    }

    [RelayCommand]
    private async Task ImportProductsFromCsvAsync()
    {
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

                if (files.Count > 0)
                {
                    var file = files[0];
                    using var stream = await file.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    string content = await reader.ReadToEndAsync();

                    int imported = await _productService.ImportFromCsvTextAsync(content);
                    IsFeedbackError = false;
                    FeedbackMessage = $"¡Se importaron correctamente {imported} productos!";
                    ProductsUpdated?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InventoryActionsViewModel: error importing CSV");
            IsFeedbackError = true;
            FeedbackMessage = "Error al importar el archivo CSV.";
        }
    }

    public Task ImportCsvCatalogAsync() => ImportProductsFromCsvAsync();

    public async Task PrintChecklistAsync()
    {
        try
        {
            var products = await _productService.GetAllAsync();
            if (products == null || products.Count == 0)
            {
                IsFeedbackError = true;
                FeedbackMessage = "No hay productos registrados en el catálogo para imprimir el checklist.";
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Checklist de Conteo Físico ({products.Count} productos)");
                    var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };
                    var confirmed = await win.ShowDialog<bool>(desktop.MainWindow);
                    if (confirmed)
                    {
                        bool printed = await _printerService.PrintInventoryChecklistAsync(products);
                        if (printed)
                        {
                            IsFeedbackError = false;
                            FeedbackMessage = $"¡Checklist de conteo físico de inventario enviado a la impresora ({products.Count} productos)!";
                        }
                        else
                        {
                            IsFeedbackError = true;
                            FeedbackMessage = "No se pudo comunicar con la impresora térmica para imprimir el checklist.";
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error printing inventory checklist");
            IsFeedbackError = true;
            FeedbackMessage = "Error al generar el checklist de inventario.";
        }
    }

    public async Task CreateSnapshotAsync()
    {
        try
        {
            string note = $"Punto de guardado - {DateTime.Now:dd/MM/yyyy HH:mm}";
            var snapshot = await _snapshotService.CreateSnapshotAsync(note);

            IsFeedbackError = false;
            FeedbackMessage = "¡Copia de seguridad del inventario creada con éxito!";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating inventory snapshot");
            IsFeedbackError = true;
            FeedbackMessage = "Error al crear la copia de seguridad del inventario.";
        }
    }

    public async Task ViewSnapshotsAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new InventorySnapshotsViewModel();
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
