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

    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<ProductDto> FilteredProducts { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _showOnlyLowStock = false;

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnShowOnlyLowStockChanged(bool value)
    {
        ApplyFilter();
    }

    [RelayCommand] public void ToggleLowStockFilter() => ShowOnlyLowStock = !ShowOnlyLowStock;
    [RelayCommand]
    public void ScanBarcode(string query)
    {
        SearchQuery = query;
        ApplyFilter();
    }

    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-ide", "pos_settings.json"
    );

    public static double LoadDefaultMinStock()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath))
            {
                var text = System.IO.File.ReadAllText(SettingsPath);
                if (double.TryParse(text.Trim(), out double val)) return val;
            }
        }
        catch { }
        return 5.0;
    }

    public static void SaveDefaultMinStock(double val)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(SettingsPath);
            if (dir != null && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(SettingsPath, val.ToString());
        }
        catch { }
    }
    [ObservableProperty] private bool _isLoading = false;

    public decimal TotalStockCost => FilteredProducts.Sum(p => p.Cost * (decimal)p.Stock);
    public decimal TotalStockSale => FilteredProducts.Sum(p => p.Price * (decimal)p.Stock);

    public InventoryCatalogViewModel(IProductService productService)
    {
        _productService = productService;
    }

    public async Task LoadProductsAsync()
    {
        try
        {
            IsLoading = true;
            var items = await _productService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Products.Clear();
                foreach (var item in items) Products.Add(item);
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InventoryCatalogViewModel: error loading products");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyFilter()
    {
        var query = SearchQuery.Trim().ToLower();
        var items = Products.AsEnumerable();

        if (ShowOnlyLowStock)
        {
            double defaultMin = 5.0;
            items = items.Where(p => p.Stock <= p.MinStock || p.Stock <= defaultMin);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(p =>
                (p.Name != null && p.Name.ToLower().Contains(query)) ||
                (p.Barcode != null && p.Barcode.ToLower().Contains(query)) ||
                (p.Category != null && p.Category.ToLower().Contains(query)));
        }

        FilteredProducts.Clear();
        foreach (var item in items) FilteredProducts.Add(item);

        OnPropertyChanged(nameof(TotalStockCost));
        OnPropertyChanged(nameof(TotalStockSale));
    }
}
