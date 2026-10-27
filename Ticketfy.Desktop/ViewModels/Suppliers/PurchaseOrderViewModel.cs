using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Suppliers;

/// <summary>
/// Handles purchase order drafting, items accumulation, and inventory replenishment confirmation.
/// Extracted from SuppliersViewModel.
/// </summary>
public partial class PurchaseOrderViewModel : ObservableObject
{
    private readonly IPurchaseService _purchaseService;
    private readonly IProductService _productService;
    private readonly ISupplierService? _supplierService;
    private long _catalogVersion;
    private long _supplierVersion;
    private long _sessionVersion;
    [ObservableProperty] private string _productSearch = string.Empty;
    [ObservableProperty] private string _supplierSearch = string.Empty;
    [ObservableProperty] private bool _preferredSupplierOnly;
    [ObservableProperty] private string _catalogMessage = string.Empty;

    public ObservableCollection<PurchaseItemDto> DraftPurchaseItems { get; } = [];
    public ObservableCollection<ProductDto> AvailableProducts { get; } = [];
    public ObservableCollection<SupplierDto> Suppliers { get; } = [];

    [ObservableProperty] private SupplierDto? _selectedSupplier;
    public SupplierDto? SelectedSupplierForPurchase
    {
        get => SelectedSupplier;
        set => SelectedSupplier = value;
    }

    [ObservableProperty] private string _invoiceNumber = string.Empty;
    [ObservableProperty] private ProductDto? _selectedProduct;
    public ProductDto? SelectedProductForPurchase
    {
        get => SelectedProduct;
        set => SelectedProduct = value;
    }

    [ObservableProperty] private string _purchaseUnitPrice = string.Empty;
    [ObservableProperty] private string _purchaseQuantity = "1";
    [ObservableProperty] private decimal _totalPurchaseCost;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;
    [ObservableProperty] private bool _isSubmitting = false;

    public string SubmitButtonText => IsSubmitting ? "GUARDANDO..." : "CONFIRMAR Y REGISTRAR COMPRA";

    public event Action<PurchaseDto>? PurchaseConfirmed;

    public PurchaseOrderViewModel(IPurchaseService purchaseService, IProductService productService, ISupplierService? supplierService = null)
    {
        _purchaseService = purchaseService;
        _productService = productService;
        _supplierService = supplierService;
    }

    public async Task LoadCatalogAsync(List<SupplierDto>? suppliers = null)
    {
        var version = ++_supplierVersion;
        try
        {
            var list = _supplierService is not null ? await _supplierService.SearchAsync(SupplierSearch) : suppliers;
            if (version != _supplierVersion) return;
            if (list != null)
            {
                var selected = SelectedSupplier;
                Suppliers.Clear();
                foreach (var s in list.Take(100)) Suppliers.Add(s);
                if (selected is not null && Suppliers.All(s => s.Id != selected.Id)) Suppliers.Add(selected);
                SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == selected?.Id);
            }
            await LoadProductsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseOrderViewModel: Error loading catalog");
            if (version != _supplierVersion) return;
            Suppliers.Clear(); AvailableProducts.Clear(); SelectedSupplier = null; SelectedProduct = null;
            IsFeedbackError = true;
            FeedbackMessage = "No se pudo consultar el catálogo. Verifique la sesión y vuelva a buscar.";
        }
    }

    partial void OnSelectedSupplierChanged(SupplierDto? value)
    {
        _ = LoadProductsAsync();
    }

    partial void OnSelectedProductChanged(ProductDto? value)
    {
        if (value != null)
        {
            PurchaseUnitPrice = value.Cost.ToString("F2");
        }
        else
        {
            PurchaseUnitPrice = string.Empty;
        }
    }

    partial void OnPreferredSupplierOnlyChanged(bool value) => _ = LoadProductsAsync();
    [RelayCommand] private Task SearchSuppliersAsync() => LoadCatalogAsync();
    [RelayCommand] private Task SearchProductsAsync() => LoadProductsAsync();
    public async Task LoadProductsAsync()
    {
        var version = ++_catalogVersion;
        try
        {
            var page = await _productService.GetPurchasePageAsync(0, 100, ProductSearch, PreferredSupplierOnly ? SelectedSupplier?.Id : null);
            if (version != _catalogVersion) return;
            var selected = SelectedProduct;
            AvailableProducts.Clear();
            foreach (var product in page.Items) AvailableProducts.Add(product);
            SelectedProduct = AvailableProducts.FirstOrDefault(p => p.Id == selected?.Id);
            CatalogMessage = page.HasMore ? "Primeras 100 coincidencias. Busque por nombre o código." : $"{page.Items.Count} productos disponibles.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Purchase product lookup failed");
            if (version != _catalogVersion) return;
            AvailableProducts.Clear(); SelectedProduct = null;
            CatalogMessage = "No se pudieron consultar los productos.";
        }
    }

    public void ClearSessionData()
    {
        ++_sessionVersion;
        SelectedSupplier = null;
        ++_catalogVersion; ++_supplierVersion;
        AvailableProducts.Clear(); Suppliers.Clear(); DraftPurchaseItems.Clear();
        SelectedProduct = null; TotalPurchaseCost = 0m;
        ProductSearch = SupplierSearch = InvoiceNumber = PurchasePaymentReference = PurchaseDueDate = FeedbackMessage = CatalogMessage = string.Empty;
        PurchasePaidAmount = 0m; PurchasePaymentMethod = "Crédito";
        _purchaseOperationId = Guid.NewGuid().ToString("N");
    }

    [RelayCommand]
    private void AddItemToDraft()
    {
        if (DraftPurchaseItems.Count >= Ticketfy.Core.Operations.CommercialDocumentLimits.Lines)
        {
            IsFeedbackError = true;
            FeedbackMessage = Ticketfy.Core.Operations.CommercialDocumentLimits.LinesMessage;
            return;
        }
        if (SelectedProduct == null)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Seleccione un producto para añadir";
            return;
        }
        if (!double.TryParse(PurchaseQuantity, out double qty) || !double.IsFinite(qty) || qty <= 0 || Math.Round(qty, 3) != qty
            || (!SelectedProduct.IsBulk && qty != Math.Truncate(qty)))
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese una cantidad válida mayor a cero";
            return;
        }
        if (!decimal.TryParse(PurchaseUnitPrice, out decimal price) || price <= 0 || decimal.Round(price, 2) != price)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese un costo unitario válido";
            return;
        }

        decimal total;
        try { total = Ticketfy.Services.Implementations.CommercialLedger.Money(price * (decimal)qty); }
        catch (OverflowException) { IsFeedbackError = true; FeedbackMessage = "La cantidad o el costo exceden el límite permitido."; return; }
        DraftPurchaseItems.Add(new PurchaseItemDto(
            Guid.NewGuid().ToString(),
            string.Empty,
            SelectedProduct.Id,
            SelectedProduct.Name,
            price,
            qty,
            total
        ));

        TotalPurchaseCost = DraftPurchaseItems.Sum(i => i.TotalPrice);
        IsFeedbackError = false;
        FeedbackMessage = string.Empty;
    }

    [RelayCommand] private void AddItemToPurchaseDraft() => AddItemToDraft();

    [RelayCommand]
    private void RemoveItemFromDraft(PurchaseItemDto item)
    {
        DraftPurchaseItems.Remove(item);
        TotalPurchaseCost = DraftPurchaseItems.Sum(i => i.TotalPrice);
    }

    [RelayCommand] private void RemoveItemFromPurchaseDraft(PurchaseItemDto item) => RemoveItemFromDraft(item);

    private string _purchaseOperationId = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _purchasePaymentMethod = "Crédito";
    [ObservableProperty] private decimal _purchasePaidAmount;
    [ObservableProperty] private string _purchasePaymentReference = string.Empty;
    [ObservableProperty] private string _purchaseDueDate = string.Empty;
    public string[] PurchasePaymentMethods { get; } = ["Crédito", "Efectivo", "Tarjeta", "Transferencia"];

    [RelayCommand]
    private async Task ConfirmPurchaseAsync()
    {
        if (IsSubmitting) return;
        if (SelectedSupplier == null)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Seleccione un proveedor obligatorio para la compra";
            return;
        }

        if (DraftPurchaseItems.Count == 0)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Agregue al menos un producto al borrador de la compra";
            return;
        }

        var version = _sessionVersion;
        try
        {
            IsSubmitting = true;
            var purchaseDto = new PurchaseDto(
                _purchaseOperationId,
                SelectedSupplier.Id,
                SelectedSupplier.Name,
                InvoiceNumber.Trim(),
                DateTimeOffset.UtcNow.ToString("O"),
                TotalPurchaseCost,
                "Entrada directa de inventario",
                DraftPurchaseItems.ToList(), OperationId: _purchaseOperationId, PaymentMethod: PurchasePaymentMethod,
                PaidAmount: PurchasePaymentMethod == "Crédito" ? 0m : PurchasePaidAmount,
                PaymentReference: PurchasePaymentReference, DueDate: PurchaseDueDate
            );

            var registered = await _purchaseService.RegisterPurchaseAsync(purchaseDto);
            if (version != _sessionVersion) return;
            _purchaseOperationId = Guid.NewGuid().ToString("N");

            // COMPLETE FORM RESET UPON SAVE
            DraftPurchaseItems.Clear();
            TotalPurchaseCost = 0;
            InvoiceNumber = string.Empty;
            SelectedSupplier = null;
            SelectedProduct = null;
            PurchaseUnitPrice = string.Empty;
            PurchaseQuantity = "1";
            PurchasePaidAmount = 0m;
            PurchasePaymentReference = PurchaseDueDate = string.Empty;
            PurchasePaymentMethod = "Crédito";

            IsFeedbackError = false;
            FeedbackMessage = "¡Compra y reabastecimiento de inventario registrados correctamente!";
            PurchaseConfirmed?.Invoke(registered);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseOrderViewModel: error registering purchase order");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}
