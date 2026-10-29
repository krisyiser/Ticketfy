using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public class ProductDialogParameters
{
    public bool IsEditMode { get; set; }
    public ProductDto? PreFilledData { get; set; }
    public bool ShowAutoFillBanner { get; set; }
    public string? PreFilledBarcode { get; set; }
}

public partial class ProductDialogViewModel : ObservableObject
{
    private readonly IProductService _productService;

    [ObservableProperty]
    private string _barcode = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitMargin))]
    private decimal? _costPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitMargin))]
    private decimal? _retailPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsStockAdjustmentReason))]
    private double? _stock = 0d;

    private string? _pendingSupplierId = null;

    [ObservableProperty]
    private string _category = "General";

    // Sprint K: Attributes & Serial / IMEI
    [ObservableProperty]
    private string _serialNumber = string.Empty;

    [ObservableProperty]
    private string _attributesText = string.Empty;

    [ObservableProperty]
    private double? _pointsRewarded = 0.0;

    [ObservableProperty]
    private double? _reorderQuantity = 10d;

    [ObservableProperty]
    private string _locationRack = string.Empty;

    [ObservableProperty]
    private string _satProductCode = string.Empty;

    [ObservableProperty]
    private string _satUnitCode = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _title = "Nuevo Producto";

    [ObservableProperty]
    private double? _minStock = 5d;

    [ObservableProperty]
    private bool _showAutoFillBanner = false;

    public string ProfitMargin
    {
        get
        {
            if (!CostsVisible) return "Costo y margen restringidos";
            decimal retail = RetailPrice ?? 0m;
            decimal cost = CostPrice ?? 0m;
            if (retail <= 0 || cost < 0) return "Margen: 0.00%";
            if (cost == 0) return "Margen: 100.00%";

            decimal margin = ((retail - cost) / retail) * 100m;
            return $"Margen: {margin:F2}%";
        }
    }

    private string? _editingProductId = null;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitMargin))]
    private bool _costsVisible = true;
    private double _originalStockSnapshot = 0.0;
    public bool NeedsStockAdjustmentReason => _editingProductId is not null && Stock != _originalStockSnapshot;

    private readonly ISessionManager? _sessionManager;
    private long _lookupVersion;
    private bool _replacingSuppliers;
    private bool _closed;

    [ObservableProperty]
    private ProductSupplierOption? _selectedSupplier;
    [ObservableProperty] private string _supplierSearch = string.Empty;
    [ObservableProperty] private string _supplierLookupMessage = string.Empty;
    [ObservableProperty] private bool _needsSupplierSearch;
    partial void OnSelectedSupplierChanged(ProductSupplierOption? value)
    {
        if (!_replacingSuppliers) _pendingSupplierId = value?.Id;
    }
    [RelayCommand] private Task SearchSuppliersAsync() => LoadCategoriesAndSuppliersAsync();
    [RelayCommand]
    private void ClearSupplier()
    { _pendingSupplierId = null; SelectedSupplier = null; }

    public System.Collections.ObjectModel.ObservableCollection<string> Categories { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<ProductSupplierOption> Suppliers { get; } = [];

    public event Action? RequestClose;

    public ProductDialogViewModel(IProductService productService, ISessionManager? sessionManager = null)
    {
        _productService = productService;
        _sessionManager = sessionManager;
        _ = LoadCategoriesAndSuppliersAsync();
    }

    public async Task LoadCategoriesAndSuppliersAsync()
    {
        var version = ++_lookupVersion;
        var sessionVersion = _sessionManager?.SessionVersion;
        bool Current() => !_closed && version == _lookupVersion && sessionVersion == _sessionManager?.SessionVersion;
        try
        {
            var options = await _productService.GetEditorOptionsAsync(_editingProductId, SupplierSearch);
            if (!Current()) return;
            Categories.Clear();
            foreach (var item in options.Categories)
            {
                Categories.Add(item);
            }

            if (!string.IsNullOrEmpty(Category) && !Categories.Contains(Category))
            {
                Categories.Add(Category);
            }

            var selected = SelectedSupplier?.Id == _pendingSupplierId ? SelectedSupplier
                : options.AssignedSupplier?.Id == _pendingSupplierId ? options.AssignedSupplier : null;
            _replacingSuppliers = true;
            try
            {
                Suppliers.Clear();
                foreach (var supplier in options.Suppliers.Items) Suppliers.Add(supplier);
                if (selected is not null && !Suppliers.Any(s => s.Id == selected.Id)) Suppliers.Add(selected);
                SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == _pendingSupplierId);
            }
            finally { _replacingSuppliers = false; }
            NeedsSupplierSearch = options.Suppliers.HasMore || !string.IsNullOrWhiteSpace(SupplierSearch);
            SupplierLookupMessage = options.Suppliers.HasMore
                ? "Primeros 100 proveedores. Busque por nombre para localizar otro."
                : string.Empty;
            if (selected?.IsActive == false) SupplierLookupMessage += " El proveedor asignado está inactivo; puede conservarlo o elegir otro.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading categories and suppliers in dialog");
            if (!Current()) return;
            _replacingSuppliers = true;
            try { Suppliers.Clear(); SelectedSupplier = null; Categories.Clear(); }
            finally { _replacingSuppliers = false; }
            NeedsSupplierSearch = true;
            SupplierLookupMessage = "No se pudieron consultar las opciones. La asignación existente se conserva al guardar; vuelva a buscar.";
        }
    }

    [ObservableProperty]
    private string _unit = "Pza";

    [ObservableProperty]
    private bool _isBulk = false;

    public System.Collections.ObjectModel.ObservableCollection<string> Units { get; } = [
        "Pza", "Kg", "Gr", "Lt", "Ml", "Mt", "Paq"
    ];

    public bool IsBulkAllowed => !string.Equals(Unit, "Pza", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(Unit, "Paq", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(Unit, "Pieza", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(Unit, "Paquete", StringComparison.OrdinalIgnoreCase);

    partial void OnUnitChanged(string value)
    {
        OnPropertyChanged(nameof(IsBulkAllowed));
        IsBulk = IsBulkAllowed;
    }

    public void LoadProductForEdit(ProductDto product)
    {
        _loadedProduct = product;
        _editingProductId = product.Id;
        Title = "Editar Producto";
        Barcode = product.Barcode ?? string.Empty;
        Name = product.Name;
        CostPrice = product.Cost;
        CostsVisible = product.CostsVisible;
        RetailPrice = product.Price;
        Stock = product.Stock;
        Category = product.Category;
        Unit = string.IsNullOrWhiteSpace(product.Unit) ? "Pza" : product.Unit;
        OnPropertyChanged(nameof(IsBulkAllowed));
        IsBulk = product.IsBulk;

        if (!string.IsNullOrEmpty(Category) && !Categories.Contains(Category))
        {
            Categories.Add(Category);
        }
        SerialNumber = product.LocationRack;
        PointsRewarded = product.PointsRewarded;
        ReorderQuantity = product.ReorderQuantity;
        LocationRack = product.LocationRack;
        SatProductCode = product.SatProductCode;
        SatUnitCode = product.SatUnitCode;
        MinStock = product.MinStock;
        MaxStock = product.MaxStock;
        TaxRate = product.TaxRate;

        _pendingSupplierId = product.DefaultSupplierId;
        _replacingSuppliers = true;
        try { SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == product.DefaultSupplierId); }
        finally { _replacingSuppliers = false; }

        _originalStockSnapshot = product.Stock;
        StockAdjustmentReason = string.Empty;
        OnPropertyChanged(nameof(NeedsStockAdjustmentReason));
        _ = LoadCategoriesAndSuppliersAsync();
    }

    public void LoadFromParameters(ProductDialogParameters parameters)
    {
        if (parameters.IsEditMode && parameters.PreFilledData != null)
        {
            LoadProductForEdit(parameters.PreFilledData);
        }
        else
        {
            _editingProductId = null;
            _loadedProduct = null;
            _pendingSupplierId = null;
            Title = "Nuevo Producto";
            ShowAutoFillBanner = parameters.ShowAutoFillBanner;
            ErrorMessage = string.Empty;
            Name = string.Empty;
            CostPrice = null;
            RetailPrice = null;
            Stock = 0d;
            Category = "General";
            Unit = "Pza";
            PointsRewarded = 0.0;
            MinStock = 5.0;
            ReorderQuantity = 10d;
            MaxStock = 0d;
            TaxRate = 0.16m;
            StockAdjustmentReason = string.Empty;
            OnPropertyChanged(nameof(NeedsStockAdjustmentReason));
            SelectedSupplier = null;
            SerialNumber = string.Empty;
            AttributesText = string.Empty;
            LocationRack = string.Empty;
            SatProductCode = string.Empty;
            SatUnitCode = string.Empty;

            if (parameters.PreFilledData != null)
            {
                Barcode = parameters.PreFilledData.Barcode ?? string.Empty;
                Name = parameters.PreFilledData.Name;
                Category = parameters.PreFilledData.Category;
                if (!string.IsNullOrEmpty(Category) && !Categories.Contains(Category))
                {
                    Categories.Add(Category);
                }
                if (!string.IsNullOrEmpty(parameters.PreFilledData.DefaultSupplierId))
                {
                    SelectedSupplier = System.Linq.Enumerable.FirstOrDefault(Suppliers, s => s.Id == parameters.PreFilledData.DefaultSupplierId);
                }
            }
            else if (!string.IsNullOrEmpty(parameters.PreFilledBarcode))
            {
                Barcode = parameters.PreFilledBarcode;
            }
            else
            {
                Barcode = string.Empty;
            }
        }
    }

    [RelayCommand]
    private void GenerateBarcode()
    {
        Barcode = "TF" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
    }

    [RelayCommand]
    private void Cancel()
    {
        _closed = true; ++_lookupVersion;
        RequestClose?.Invoke();
    }

    private readonly string _newProductId = Guid.NewGuid().ToString("N");
    private ProductDto? _loadedProduct;
    [ObservableProperty] private string _stockAdjustmentReason = string.Empty;
    [ObservableProperty] private double _maxStock;
    [ObservableProperty] private decimal _taxRate = 0.16m;
    private bool _saving;
    private bool _saved;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_saving || _saved || _closed) return;
        var sessionVersion = _sessionManager?.SessionVersion;
        _saving = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Barcode))
            {
                ErrorMessage = "Código y nombre son obligatorios.";
                return;
            }

            if (RetailPrice is null || CostsVisible && CostPrice is null || Stock is null || MinStock is null)
            {
                ErrorMessage = "Complete costo, precio, stock actual y stock mínimo con números válidos.";
                return;
            }
            if (NeedsStockAdjustmentReason && string.IsNullOrWhiteSpace(StockAdjustmentReason))
            {
                ErrorMessage = "Indique el motivo del cambio de existencias para registrarlo en el historial.";
                return;
            }

            var dto = new ProductDto(
                _editingProductId ?? _newProductId, Barcode, Name, CostPrice ?? 0, RetailPrice ?? 0,
                WholesalePrice: _loadedProduct?.WholesalePrice ?? 0m, WholesaleThreshold: _loadedProduct?.WholesaleThreshold ?? 0,
                Stock: Stock ?? 0, Category: Category, Unit: Unit,
                PointsRewarded: PointsRewarded ?? 0.0,
                ReorderQuantity: ReorderQuantity ?? 10.0,
                LocationRack: LocationRack,
                SatProductCode: SatProductCode,
                SatUnitCode: SatUnitCode,
                MinStock: MinStock ?? 5.0,
                DefaultSupplierId: _pendingSupplierId,
                IsBulk: IsBulk, IsKit: _loadedProduct?.IsKit ?? false, IsActive: _loadedProduct?.IsActive ?? true, MaxStock: MaxStock, TaxRate: TaxRate, StockAdjustmentReason: StockAdjustmentReason,
                ExpectedStockVersion: _loadedProduct?.ExpectedStockVersion
            )
            { CostsVisible = CostsVisible };

            if (_editingProductId != null)
            {
                await _productService.UpdateAsync(dto);

            }
            else
            {
                await _productService.AddAsync(dto);
            }

            _saved = true;
            if (!_closed && sessionVersion == _sessionManager?.SessionVersion) RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving product");
            if (_closed || sessionVersion != _sessionManager?.SessionVersion) return;
            var baseMsg = ex.GetBaseException().Message;
            if (baseMsg.Contains("UNIQUE constraint failed: products.barcode", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = $"El código de barras '{Barcode}' ya está registrado en otro producto.";
            }
            else
            {
                ErrorMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex, "No se pudo guardar el producto. Revise los datos e inténtelo de nuevo.");
            }
        }
        finally { _saving = false; }
    }
}
