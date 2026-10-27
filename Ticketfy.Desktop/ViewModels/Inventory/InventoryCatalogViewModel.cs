using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Inventory;

/// <summary>
/// Manages product catalog filtering, searching, and stock metrics.
/// Extracted from InventoryViewModel.
/// </summary>
public partial class InventoryCatalogViewModel : ObservableObject
{
    private readonly IProductService _productService;
    private int _loadVersion;
    [ObservableProperty] private int _page;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _loadMessage = "";
    private decimal _stockCost, _stockSale;
    private bool _costsVisible;
    public void ClearSessionData()
    {
        SearchQuery = string.Empty;
        ++_loadVersion;
        Page = 0;
        Products.Clear(); FilteredProducts.Clear();
        _stockCost = _stockSale = 0m; _costsVisible = false;
        HasMore = false; IsLoading = false; LoadMessage = string.Empty;
        OnPropertyChanged(nameof(TotalStockCost)); OnPropertyChanged(nameof(TotalStockCostDisplay));
        OnPropertyChanged(nameof(TotalStockSale)); OnPropertyChanged(nameof(PageLabel));
    }
    public string PageLabel => $"Página {Page + 1} · hasta 100 productos · totales de TODOS los resultados físicos filtrados";

    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<ProductDto> FilteredProducts { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _showOnlyLowStock = false;

    partial void OnSearchQueryChanged(string value)
    {
        Page = 0; _ = ReloadAfterSearchAsync();
    }

    partial void OnShowOnlyLowStockChanged(bool value)
    {
        Page = 0; _ = LoadProductsAsync();
    }

    [RelayCommand] public void ToggleLowStockFilter() => ShowOnlyLowStock = !ShowOnlyLowStock;
    [RelayCommand]
    public void ScanBarcode(string query)
    {
        SearchQuery = query;
    }

    public double DefaultMinStock { get; private set; } = 5d;
    public async Task SetDefaultMinStockAsync(double value)
    {
        await _productService.SetDefaultMinStockAsync(value);
        DefaultMinStock = value; Page = 0; await LoadProductsAsync();
    }
    [ObservableProperty] private bool _isLoading = false;

    public decimal TotalStockCost => _stockCost;
    public string TotalStockCostDisplay => _costsVisible ? TotalStockCost.ToString("C2") : "Restringido";
    public decimal TotalStockSale => _stockSale;
    [RelayCommand] private async Task PreviousPageAsync() { if (Page > 0 && !IsLoading) { Page--; await LoadProductsAsync(); } }
    [RelayCommand] private async Task NextPageAsync() { if (HasMore && !IsLoading) { Page++; await LoadProductsAsync(); } }
    private async Task ReloadAfterSearchAsync()
    {
        int version = ++_loadVersion;
        await Task.Delay(150);
        if (version == _loadVersion) await LoadProductsAsync();
    }

    public InventoryCatalogViewModel(IProductService productService)
    {
        _productService = productService;
    }

    public async Task LoadProductsAsync()
    {
        int version = ++_loadVersion;
        try
        {
            IsLoading = true;
            DefaultMinStock = await _productService.GetDefaultMinStockAsync();
            if (version != _loadVersion) return;
            var result = await _productService.GetInventoryPageAsync(Page, search: SearchQuery, lowStockOnly: ShowOnlyLowStock);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                Products.Clear(); FilteredProducts.Clear();
                foreach (var item in result.Items) { Products.Add(item); FilteredProducts.Add(item); }
                HasMore = result.HasMore; _stockCost = result.StockCost; _stockSale = result.StockSale; _costsVisible = result.CostsVisible;
                LoadMessage = "";
                OnPropertyChanged(nameof(TotalStockCost)); OnPropertyChanged(nameof(TotalStockCostDisplay));
                OnPropertyChanged(nameof(TotalStockSale)); OnPropertyChanged(nameof(PageLabel));
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InventoryCatalogViewModel: error loading products");
            if (version == _loadVersion)
            {
                ClearSessionData();
                LoadMessage = "No se pudo actualizar el inventario. Verifique permisos y conexión a la base.";
            }
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    public void ApplyFilter() { Page = 0; _ = LoadProductsAsync(); }
}
