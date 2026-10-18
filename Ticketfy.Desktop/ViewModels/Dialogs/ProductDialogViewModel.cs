using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Data;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Ticketfy.Data.Entities;
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
    private double? _stock;

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
    private double? _reorderQuantity;

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
    private double? _minStock;

    [ObservableProperty]
    private bool _showAutoFillBanner = false;

    public string ProfitMargin
    {
        get
        {
            decimal retail = RetailPrice ?? 0m;
            decimal cost = CostPrice ?? 0m;
            if (retail <= 0 || cost < 0) return "Margen: 0.00%";
            if (cost == 0) return "Margen: 100.00%";

            decimal margin = ((retail - cost) / retail) * 100m;
            return $"Margen: {margin:F2}%";
        }
    }

    private string? _editingProductId = null;
    private double _originalStockSnapshot = 0.0;
    private double _originalMinStockSnapshot = 5.0;

    private readonly ISessionManager? _sessionManager;
    private readonly IAuditService? _auditService;
    private readonly IDbContextFactory<AppDbContext> _factory;

    [ObservableProperty]
    private SupplierDto? _selectedSupplier;

    public System.Collections.ObjectModel.ObservableCollection<string> Categories { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<SupplierDto> Suppliers { get; } = [];

    public event Action? RequestClose;

    public ProductDialogViewModel(IProductService productService, AppDbContext db, ISessionManager? sessionManager = null, IAuditService? auditService = null)
    {
        _productService = productService;
        _factory = new AppDbContextFactoryImpl<AppDbContext>(db.Options);
        _sessionManager = sessionManager;
        _auditService = auditService;
        _ = LoadCategoriesAndSuppliersAsync();
    }

    public async Task LoadCategoriesAndSuppliersAsync()
    {
        await using var _db = await _factory.CreateDbContextAsync();
        try
        {
            var list = await _db.Categories
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => c.Name)
                .ToListAsync();

            Categories.Clear();
            foreach (var item in list)
            {
                Categories.Add(item);
            }

            if (!string.IsNullOrEmpty(Category) && !Categories.Contains(Category))
            {
                Categories.Add(Category);
            }

            var supplierEntities = await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.IsActive == 1)
                .OrderBy(s => s.Name)
                .ToListAsync();

            Suppliers.Clear();
            foreach (var s in supplierEntities)
            {
                Suppliers.Add(new SupplierDto(s.Id, s.Name, s.Rfc, s.Phone, s.Email, s.Address, s.ContactPerson));
            }

            if (!string.IsNullOrEmpty(_pendingSupplierId))
            {
                SelectedSupplier = System.Linq.Enumerable.FirstOrDefault(Suppliers, s => s.Id == _pendingSupplierId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading categories and suppliers in dialog");
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
        RetailPrice = product.Price;
        Stock = product.Stock;
        Category = product.Category;
        Unit = string.IsNullOrWhiteSpace(product.Unit) ? "Pza" : product.Unit;
        OnPropertyChanged(nameof(IsBulkAllowed));
        IsBulk = IsBulkAllowed;

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

        if (!string.IsNullOrEmpty(product.DefaultSupplierId))
        {
            _pendingSupplierId = product.DefaultSupplierId;
            if (Suppliers.Count > 0)
            {
                SelectedSupplier = System.Linq.Enumerable.FirstOrDefault(Suppliers, s => s.Id == product.DefaultSupplierId);
            }
        }

        _originalStockSnapshot = product.Stock;
        _originalMinStockSnapshot = product.MinStock;
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
            Title = "Nuevo Producto";
            ShowAutoFillBanner = parameters.ShowAutoFillBanner;
            ErrorMessage = string.Empty;
            Name = string.Empty;
            CostPrice = null;
            RetailPrice = null;
            Stock = null;
            Category = "General";
            Unit = "Pza";
            PointsRewarded = 0.0;
            MinStock = 5.0;
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
        if (_saving || _saved) return;
        _saving = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Barcode))
            {
                ErrorMessage = "Código y nombre son obligatorios.";
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
                DefaultSupplierId: SelectedSupplier?.Id,
                IsBulk: IsBulk, IsKit: _loadedProduct?.IsKit ?? false, MaxStock: MaxStock, TaxRate: TaxRate, StockAdjustmentReason: StockAdjustmentReason,
                ExpectedStockVersion: _loadedProduct?.ExpectedStockVersion
            );

            if (_editingProductId != null)
            {
                await _productService.UpdateAsync(dto);

            }
            else
            {
                await _productService.AddAsync(dto);
            }

            _saved = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving product");
            var baseMsg = ex.GetBaseException().Message;
            if (baseMsg.Contains("UNIQUE constraint failed: products.barcode", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = $"El código de barras '{Barcode}' ya está registrado en otro producto.";
            }
            else
            {
                ErrorMessage = baseMsg;
            }
        }
        finally { _saving = false; }
    }
}
