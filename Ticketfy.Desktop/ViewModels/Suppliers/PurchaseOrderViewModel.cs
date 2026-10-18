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

    private List<ProductDto> _allProductsCache = [];

    public PurchaseOrderViewModel(IPurchaseService purchaseService, IProductService productService)
    {
        _purchaseService = purchaseService;
        _productService = productService;
    }

    public async Task LoadCatalogAsync(List<SupplierDto>? suppliers = null)
    {
        try
        {
            var products = await _productService.GetAllAsync();
            _allProductsCache = products;

            if (suppliers != null)
            {
                Suppliers.Clear();
                foreach (var s in suppliers) Suppliers.Add(s);
            }

            FilterProductsBySelectedSupplier();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseOrderViewModel: Error loading catalog");
        }
    }

    partial void OnSelectedSupplierChanged(SupplierDto? value)
    {
        FilterProductsBySelectedSupplier();
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

    private void FilterProductsBySelectedSupplier()
    {
        AvailableProducts.Clear();
        if (SelectedSupplier == null)
        {
            foreach (var p in _allProductsCache) AvailableProducts.Add(p);
        }
        else
        {
            var matchingProducts = _allProductsCache
                .Where(p => p.DefaultSupplierId == SelectedSupplier.Id)
                .ToList();

            if (matchingProducts.Count > 0)
            {
                foreach (var p in matchingProducts) AvailableProducts.Add(p);
            }
            else
            {
                foreach (var p in _allProductsCache) AvailableProducts.Add(p);
            }
        }
    }

    [RelayCommand]
    private void AddItemToDraft()
    {
        if (SelectedProduct == null)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Seleccione un producto para añadir";
            return;
        }
        if (!double.TryParse(PurchaseQuantity, out double qty) || qty <= 0)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese una cantidad válida mayor a cero";
            return;
        }
        if (!decimal.TryParse(PurchaseUnitPrice, out decimal price) || price < 0)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese un costo unitario válido";
            return;
        }

        var total = price * (decimal)qty;
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

        try
        {
            IsSubmitting = true;
            var purchaseDto = new PurchaseDto(
                _purchaseOperationId,
                SelectedSupplier.Id,
                SelectedSupplier.Name,
                InvoiceNumber.Trim(),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TotalPurchaseCost,
                "Entrada directa de inventario",
                DraftPurchaseItems.ToList(), OperationId: _purchaseOperationId, PaymentMethod: PurchasePaymentMethod,
                PaidAmount: PurchasePaymentMethod == "Crédito" ? 0m : PurchasePaidAmount,
                PaymentReference: PurchasePaymentReference, DueDate: PurchaseDueDate
            );

            var registered = await _purchaseService.RegisterPurchaseAsync(purchaseDto);
            _purchaseOperationId = Guid.NewGuid().ToString("N");

            // COMPLETE FORM RESET UPON SAVE
            DraftPurchaseItems.Clear();
            TotalPurchaseCost = 0;
            InvoiceNumber = string.Empty;
            SelectedSupplier = null;
            SelectedProduct = null;
            PurchaseUnitPrice = string.Empty;
            PurchaseQuantity = "1";

            IsFeedbackError = false;
            FeedbackMessage = "¡Compra y reabastecimiento de inventario registrados correctamente!";
            PurchaseConfirmed?.Invoke(registered);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseOrderViewModel: error registering purchase order");
            IsFeedbackError = true;
            FeedbackMessage = ex.Message;
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}
