using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.State;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class CatalogViewModel : ObservableObject
{
    private readonly IProductService _productService;
    private readonly IExternalCatalogService _externalCatalogService;
    private readonly IItemKitService? _kitService;
    private readonly CartStateStore _cartStateStore;
    public event Action<Ticketfy.ViewModels.Dialogs.ProductDialogParameters>? OpenProductDialogWithParamsRequested;

    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<ProductDto> FilteredProducts { get; } = [];
    public ObservableCollection<CategoryChipDto> CategoryChips { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedCategory = "⭐ Top Ventas";
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackColor))]
    private bool _feedbackIsError;

    public string FeedbackColor => FeedbackIsError ? "#EF4444" : "#10B981";

    public CatalogViewModel(IProductService productService, IExternalCatalogService externalCatalogService, IItemKitService? kitService, CartStateStore cartStateStore)
    {
        _productService = productService;
        _externalCatalogService = externalCatalogService;
        _kitService = kitService;
        _cartStateStore = cartStateStore;
    }

    public async Task LoadProductsAsync()
    {
        try
        {
            var list = await _productService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Products.Clear();
                foreach (var p in list) Products.Add(p);
                BuildCategoryChips();
                FilterProducts();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading products");
        }
    }

    private void BuildCategoryChips()
    {
        CategoryChips.Clear();
        CategoryChips.Add(new CategoryChipDto("⭐ Top Ventas", Products.Count, $"⭐ TOP VENTAS ({Products.Count})"));

        var promoCount = Products.Count(p => string.Equals(p.Category, "Promociones", StringComparison.OrdinalIgnoreCase));
        if (promoCount > 0)
        {
            CategoryChips.Add(new CategoryChipDto("Promociones", promoCount, $"🏷️ PROMOCIONES ({promoCount})"));
        }

        var groups = Products.Where(p => !string.Equals(p.Category, "Promociones", StringComparison.OrdinalIgnoreCase))
                             .GroupBy(p => p.Category ?? "General")
                             .OrderBy(g => g.Key);

        foreach (var g in groups)
        {
            CategoryChips.Add(new CategoryChipDto(g.Key, g.Count(), $"{g.Key.ToUpper()} ({g.Count()})"));
        }
    }

    [RelayCommand]
    private void SelectCategoryChip(CategoryChipDto chip)
    {
        if (chip == null) return;
        SelectedCategory = chip.Name;
        FilterProducts();
    }

    private void FilterProducts()
    {
        FilteredProducts.Clear();
        var query = SearchQuery.Trim().ToLower();

        var matches = Products.Where(p =>
            (SelectedCategory == "⭐ Top Ventas" || SelectedCategory == "Todos" || string.Equals(p.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query) ||
             p.Name.ToLower().Contains(query) ||
             (p.Barcode != null && p.Barcode.ToLower().Contains(query)))
        );

        foreach (var m in matches) FilteredProducts.Add(m);
    }

    partial void OnSearchQueryChanged(string value) => FilterProducts();

    [RelayCommand]
    private void FocusSearch() => WeakReferenceMessenger.Default.Send(new FocusSearchMessage());

    [RelayCommand]
    private async Task ProcessScanOrSearchSubmit()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;

            var input = SearchQuery.Trim();
            double quantityMultiplier = 1.0;
            string productQuery = input;

            var parts = input.Split('*', 2);
            if (parts.Length == 2 && double.TryParse(parts[0], out double parsedQty))
            {
                quantityMultiplier = parsedQty;
                productQuery = parts[1].Trim();
            }

            var p = Products.FirstOrDefault(x =>
                (x.Barcode != null && x.Barcode.Equals(productQuery, StringComparison.OrdinalIgnoreCase)) ||
                x.Name.Equals(productQuery, StringComparison.OrdinalIgnoreCase));

            if (p == null) p = Products.FirstOrDefault(x => x.Name.Contains(productQuery, StringComparison.OrdinalIgnoreCase));

            if (p != null)
            {
                SearchQuery = string.Empty;
                if (quantityMultiplier != 1.0)
                {
                    AddToCartWithQuantity(p, quantityMultiplier);
                }
                else if (IsBulkProduct(p))
                {
                    OpenBulkWeightRequested?.Invoke(p);
                }
                else
                {
                    AddToCartWithQuantity(p, 1.0);
                }
            }
            else
            {
                await TryAddKitBarcodeAsync(productQuery, quantityMultiplier);
            }
        }
        finally
        {
            WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
        }
    }

    private async Task TryAddKitBarcodeAsync(string barcode, double multiplier)
    {
        bool kitFound = false;
        if (_kitService != null)
        {
            var kit = await _kitService.GetByBarcodeAsync(barcode);
            if (kit != null)
            {
                kitFound = true;
                foreach (var item in kit.Items)
                {
                    var prod = Products.FirstOrDefault(p => p.Id == item.ProductId);
                    if (prod != null)
                    {
                        AddToCartWithQuantity(prod, item.Quantity * multiplier);
                    }
                }
                SearchQuery = string.Empty;
                FeedbackMessage = $"¡Combo / Paquete '{kit.Name}' agregado al ticket!";
            }
        }

        if (!kitFound)
        {
            FeedbackMessage = $"Buscando '{barcode}' en OpenFoodFacts...";
            var externalProduct = await _externalCatalogService.FetchProductByBarcodeAsync(barcode);

            if (externalProduct != null)
            {
                OpenProductDialogWithParamsRequested?.Invoke(new Ticketfy.ViewModels.Dialogs.ProductDialogParameters
                {
                    IsEditMode = false,
                    PreFilledData = externalProduct,
                    ShowAutoFillBanner = true
                });
            }
            else
            {
                OpenProductDialogWithParamsRequested?.Invoke(new Ticketfy.ViewModels.Dialogs.ProductDialogParameters
                {
                    IsEditMode = false,
                    PreFilledBarcode = barcode
                });
            }
            SearchQuery = string.Empty;
        }
    }

    public event Action<ProductDto>? OpenBulkWeightRequested;

    [RelayCommand]
    private void AddToCart(ProductDto product)
    {
        if (product == null) return;

        if (IsBulkProduct(product))
        {
            OpenBulkWeightRequested?.Invoke(product);
        }
        else
        {
            AddToCartWithQuantity(product, 1.0);
        }
    }

    public void DirectAddToCart(ProductDto product, double qty)
    {
        AddToCartWithQuantity(product, qty);
    }

    private static bool IsBulkProduct(ProductDto p)
    {
        if (p == null) return false;
        if (p.IsBulk) return true;
        var u = p.Unit ?? "";
        return u.Equals("kg", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("gr", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("lt", StringComparison.OrdinalIgnoreCase) ||
               u.Equals("mt", StringComparison.OrdinalIgnoreCase);
    }

    private void AddToCartWithQuantity(ProductDto product, double qty)
    {
        if (product == null) return;

        var cartItem = new CartItemDto(product.Id, product.Name, product.Price, qty, product.Unit, product.PointsRewarded)
        {
            Category = product.Category ?? "General",
            Cost = product.Cost,
            OriginalUnitPrice = product.Price,
            PointsRewarded = product.PointsRewarded,
            SatProductCode = product.SatProductCode ?? "01010101",
            SatUnitCode = product.SatUnitCode ?? "H87"
        };

        _cartStateStore.AddItem(cartItem, product.Stock);

        FeedbackIsError = false;
        FeedbackMessage = $"Agregado: {product.Name} ({qty:N3} {product.Unit})";
    }
}
