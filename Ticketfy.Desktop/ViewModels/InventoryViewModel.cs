using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels.Inventory;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

/// <summary>
/// Modular coordinator for the Inventory screen.
/// Composes Catalog, Actions (CSV/Snapshots), and Predictive Intelligence sub-ViewModels.
/// </summary>
public partial class InventoryViewModel : ObservableObject
{
    private readonly IProductService _productService;

    public InventoryCatalogViewModel CatalogVM { get; }
    public InventoryActionsViewModel ActionsVM { get; }
    public InventoryIntelligenceViewModel IntelligenceVM { get; }

    public bool ShowOnlyLowStock
    {
        get => CatalogVM.ShowOnlyLowStock;
        set => CatalogVM.ShowOnlyLowStock = value;
    }

    public string SearchQuery
    {
        get => CatalogVM.SearchQuery;
        set
        {
            if (CatalogVM.SearchQuery != value)
            {
                CatalogVM.SearchQuery = value;
                OnPropertyChanged(nameof(SearchQuery));
                OnPropertyChanged(nameof(FilteredProducts));
                OnPropertyChanged(nameof(TotalStockCost));
                OnPropertyChanged(nameof(TotalStockSale));
            }
        }
    }

    public ObservableCollection<ProductDto> FilteredProducts => CatalogVM.FilteredProducts;
    public decimal TotalStockCost => CatalogVM.TotalStockCost;
    public decimal TotalStockSale => CatalogVM.TotalStockSale;

    public string FeedbackMessage => ActionsVM.FeedbackMessage;
    public bool IsFeedbackError => ActionsVM.IsFeedbackError;
    public bool IsIntelligencePanelVisible => IntelligenceVM.IsIntelligencePanelVisible;
    public ObservableCollection<PredictiveAlertDto> UrgentRestockAlerts => IntelligenceVM.UrgentRestockAlerts;

    public static double LoadDefaultMinStock() => InventoryCatalogViewModel.LoadDefaultMinStock();
    public static void SaveDefaultMinStock(double val) => InventoryCatalogViewModel.SaveDefaultMinStock(val);

    public event Action? OpenAddProductRequested;
    public event Action<Ticketfy.ViewModels.Dialogs.ProductDialogParameters>? OpenProductDialogWithParamsRequested;
    public event Action<ProductDto>? OpenEditProductRequested;
    public event Action? OpenConfigureLowStockRequested;
    public event Action? OpenManageCategoriesRequested;

    [RelayCommand] private void ToggleLowStockFilter() => CatalogVM.ToggleLowStockFilterCommand.Execute(null);
    [RelayCommand] private void ScanBarcode(string query) => CatalogVM.ScanBarcodeCommand.Execute(query);

    [RelayCommand] private async Task ImportCsvCatalogAsync() => await ActionsVM.ImportCsvCatalogAsync();
    [RelayCommand] private async Task PrintChecklistAsync() => await ActionsVM.PrintChecklistAsync();
    [RelayCommand] private async Task CreateSnapshotAsync() => await ActionsVM.CreateSnapshotAsync();
    [RelayCommand] private async Task ViewSnapshotsAsync() => await ActionsVM.ViewSnapshotsAsync();
    [RelayCommand] private void ClearFeedback() => ActionsVM.ClearFeedbackCommand.Execute(null);

    public InventoryViewModel(IProductService productService, IExternalCatalogService externalCatalogService, IPurchaseService? purchaseService = null, IPredictiveIntelligenceService? predictiveService = null, IEscPosPrinterService? printerService = null)
    {
        _productService = productService;
        CatalogVM = new InventoryCatalogViewModel(productService);
        ActionsVM = new InventoryActionsViewModel(productService, externalCatalogService, printerService: printerService);
        IntelligenceVM = new InventoryIntelligenceViewModel(predictiveService);

        ActionsVM.ProductsUpdated += async () => await LoadProductsAsync();
        ActionsVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ActionsVM.FeedbackMessage))
                OnPropertyChanged(nameof(FeedbackMessage));
            if (e.PropertyName == nameof(ActionsVM.IsFeedbackError))
                OnPropertyChanged(nameof(IsFeedbackError));
        };
        _ = LoadProductsAsync();
    }

    public async Task LoadProductsAsync()
    {
        await CatalogVM.LoadProductsAsync();
        await IntelligenceVM.RefreshAlertsAsync();
    }

    [RelayCommand] private void OpenAddProductDialog() => OpenAddProductRequested?.Invoke();
    [RelayCommand] private void EditProduct(ProductDto product) => OpenEditProductRequested?.Invoke(product);
    [RelayCommand] private void OpenManageCategoriesDialog() => OpenManageCategoriesRequested?.Invoke();

    [RelayCommand]
    private async Task DeleteProductAsync(ProductDto product)
    {
        if (product == null) return;
        try
        {
            await _productService.DeleteAsync(product.Id);
            await LoadProductsAsync();
        }
        catch { }
    }

    [RelayCommand] private void OpenConfigureLowStockDialog() => OpenConfigureLowStockRequested?.Invoke();
    public void OpenProductDialogWithParams(Ticketfy.ViewModels.Dialogs.ProductDialogParameters parameters) => OpenProductDialogWithParamsRequested?.Invoke(parameters);
}
