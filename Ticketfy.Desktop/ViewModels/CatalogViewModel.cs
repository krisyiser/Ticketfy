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
    private readonly CartStateStore _cartStateStore;
    public event Action<Ticketfy.ViewModels.Dialogs.ProductDialogParameters>? OpenProductDialogWithParamsRequested;

    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<ProductDto> FilteredProducts { get; } = [];
    public ObservableCollection<CategoryChipDto> CategoryChips { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedCategory = "Todos";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasNextPage;
    [ObservableProperty] private string _pageLabel = "Página 1";
    private int _page, _queryVersion;
    private bool _clearingSession;
    private const int PageSize = 60;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackColor))]
    private bool _feedbackIsError;

    public string FeedbackColor => FeedbackIsError ? "#EF4444" : "#10B981";

    public CatalogViewModel(IProductService productService, IExternalCatalogService externalCatalogService, CartStateStore cartStateStore)
    {
        _productService = productService;
        _externalCatalogService = externalCatalogService;
        _cartStateStore = cartStateStore;
    }

    public async Task LoadProductsAsync()
    {
        int version = ++_queryVersion;
        try
        {
            var categories = await _productService.GetCategoryCountsAsync();
            if (version != _queryVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CategoryChips.Clear();
                CategoryChips.Add(new CategoryChipDto("Todos", categories.Sum(c => c.Count), $"TODOS ({categories.Sum(c => c.Count)})"));
                foreach (var category in categories) CategoryChips.Add(new CategoryChipDto(category.Name, category.Count, $"{category.Name} ({category.Count})"));
            });
            _page = 0;
            await LoadPageAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading products");
            Products.Clear(); FilteredProducts.Clear(); FeedbackIsError = true;
            FeedbackMessage = "No se pudo consultar el catálogo. Revise sesión y permisos y vuelva a cargar.";
        }
    }

    public void ClearSessionData()
    {
        ++_queryVersion;
        _clearingSession = true;
        try { SearchQuery = ""; SelectedCategory = "Todos"; _page = 0; }
        finally { _clearingSession = false; }
        Products.Clear(); FilteredProducts.Clear(); CategoryChips.Clear();
        FeedbackMessage = ""; HasNextPage = false; IsLoading = false;
    }

    [RelayCommand]
    private async Task SelectCategoryChip(CategoryChipDto chip)
    {
        if (chip == null) return;
        SelectedCategory = chip.Name;
        _page = 0;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync(bool debounce = false)
    {
        int version = ++_queryVersion;
        IsLoading = true;
        try
        {
            if (debounce) await Task.Delay(150);
            if (version != _queryVersion) return;
            var result = await _productService.GetPageAsync(_page, PageSize, SearchQuery, SelectedCategory == "Todos" ? null : SelectedCategory);
            if (version != _queryVersion) return;
            Products.Clear(); FilteredProducts.Clear();
            foreach (var product in result.Items) { Products.Add(product); FilteredProducts.Add(product); }
            HasNextPage = result.HasMore;
            PageLabel = $"Página {_page + 1} · {result.Items.Count} productos · búsqueda en todo el catálogo";
        }
        catch (Exception ex)
        {
            if (version != _queryVersion) return;
            Products.Clear(); FilteredProducts.Clear(); HasNextPage = false;
            FeedbackIsError = true; FeedbackMessage = "No se pudo consultar el catálogo. Revise la sesión e intente de nuevo.";
            Log.Error(ex, "Catalog page query failed");
        }
        finally { if (version == _queryVersion) IsLoading = false; }
    }

    partial void OnSearchQueryChanged(string value) { if (_clearingSession) return; _page = 0; _ = LoadPageAsync(debounce: true); }

    [RelayCommand]
    private async Task NextPageAsync() { if (IsLoading || !HasNextPage) return; _page++; await LoadPageAsync(); }
    [RelayCommand]
    private async Task PreviousPageAsync() { if (IsLoading || _page == 0) return; _page--; await LoadPageAsync(); }

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
            if (!double.IsFinite(quantityMultiplier) || quantityMultiplier <= 0 || Math.Round(quantityMultiplier, 3) != quantityMultiplier)
                throw new InvalidOperationException("Cantidad inválida: use un número positivo con hasta tres decimales.");
            var p = await _productService.GetByBarcodeAsync(productQuery);
            if (p is null)
            {
                var matches = await _productService.GetPageAsync(0, 2, productQuery);
                if (matches.Items.Count == 1 && !matches.HasMore && matches.Items[0].Name.Equals(productQuery, StringComparison.OrdinalIgnoreCase)) p = matches.Items[0];
                else if (matches.Items.Count > 0)
                {
                    FeedbackIsError = false;
                    FeedbackMessage = "Seleccione el producto en los resultados. Una búsqueda parcial no agrega automáticamente el primer resultado.";
                    return;
                }
            }

            if (p != null)
            {
                SearchQuery = string.Empty;
                if (p.InventoryWarning is not null) throw new InvalidOperationException(p.InventoryWarning);
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
                await TryDiscoverProductAsync(productQuery);
            }
        }
        catch (Exception ex)
        {
            FeedbackIsError = true; FeedbackMessage = ex.Message;
            Log.Error(ex, "Scan or product lookup failed");
        }
        finally
        {
            WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
        }
    }

    private async Task TryDiscoverProductAsync(string barcode)
    {
        // Kits are products in the authoritative catalog. Never substitute separate ingredient sales.
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

    public event Action<ProductDto>? OpenBulkWeightRequested;

    [RelayCommand]
    private void AddToCart(ProductDto product)
    {
        if (product == null) return;
        if (product.InventoryWarning is not null)
        {
            FeedbackIsError = true; FeedbackMessage = product.InventoryWarning; return;
        }

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
        if (product.InventoryWarning is not null)
        {
            FeedbackIsError = true; FeedbackMessage = product.InventoryWarning; return;
        }

        if (!product.IsActive || !double.IsFinite(qty) || qty <= 0 || Math.Round(qty, 3) != qty
            || (!IsBulkProduct(product) && Math.Truncate(qty) != qty))
        {
            FeedbackIsError = true; FeedbackMessage = "Producto inactivo o cantidad inválida para su unidad."; return;
        }

        var cartItem = new CartItemDto(product.Id, product.Name, product.Price, qty, product.Unit, product.PointsRewarded)
        {
            Category = product.Category ?? "General",
            Cost = product.Cost,
            OriginalUnitPrice = product.Price,
            PointsRewarded = product.PointsRewarded,
            SatProductCode = product.SatProductCode ?? "01010101",
            SatUnitCode = product.SatUnitCode ?? "H87"
        };

        try { _cartStateStore.AddItem(cartItem, product.Stock); }
        catch (InvalidOperationException ex) { FeedbackIsError = true; FeedbackMessage = ex.Message; return; }

        FeedbackIsError = false;
        FeedbackMessage = $"Agregado: {product.Name} ({qty:N3} {product.Unit})";
    }
}
