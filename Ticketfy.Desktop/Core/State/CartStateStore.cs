using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.State;

public partial class CartStateStore : ObservableObject
{
    public Ticketfy.Services.Interfaces.IPromotionService? PromotionService { get; set; }
    private long _revision;
    private long _pricingRequest;
    internal long Revision => _revision;
    private bool _applyingPromotions;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PricingFeedback))] private string _pricingError = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PricingFeedback))] private bool _isPricing;
    public string PricingFeedback => IsPricing ? "Actualizando promociones..." : string.IsNullOrEmpty(PricingError)
        ? string.Empty : "No se pudieron actualizar las promociones. Pulse Cobrar para reintentar.";
    public ObservableCollection<CartItemDto> Items { get; } = new();

    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _total;
    [ObservableProperty] private decimal _discountTotal;

    public event System.Action<string>? ProductAddedToCart;

    public void AddItem(CartItemDto item, double absoluteDbStock)
    {
        if (!double.IsFinite(item.Quantity) || item.Quantity <= 0 || Math.Round(item.Quantity, 3) != item.Quantity
            || !double.IsFinite(absoluteDbStock) || absoluteDbStock < 0)
            throw new InvalidOperationException("Cantidad o existencia inválida.");
        var existing = Items.FirstOrDefault(i => i.ProductId == item.ProductId);
        if (existing != null)
        {
            existing.AvailableStock = absoluteDbStock;
            var result = existing.IncreaseQuantity(item.Quantity, absoluteDbStock);
            if (!result.Success)
            {
                throw new System.InvalidOperationException(result.ErrorMessage);
            }
        }
        else
        {
            Ticketfy.Core.Operations.CommercialDocumentLimits.RequireLineCount(Items.Count + 1);
            if (item.Quantity <= 0 || item.Quantity > absoluteDbStock) throw new System.InvalidOperationException("Stock insuficiente.");
            item.AvailableStock = absoluteDbStock;
            Items.Add(item);
        }
        RecalculateTotals();
        ProductAddedToCart?.Invoke(item.ProductId);
    }

    public void RemoveItem(CartItemDto item)
    {
        Items.Remove(item);
        RecalculateTotals();
    }

    public void Clear()
    {
        Items.Clear();
        PricingError = string.Empty;
        RecalculateTotals();
    }

    public void RecalculateTotals()
    {
        Subtotal = Items.Sum(i => Math.Round(i.OriginalUnitPrice * (decimal)i.Quantity, 2, MidpointRounding.ToEven));
        Total = Items.Sum(i => i.GetLineTotal());
        DiscountTotal = Subtotal - Total;
        if (!_applyingPromotions)
        {
            _revision++;
            _ = RefreshPromotionsAsync();
        }
    }

    public async System.Threading.Tasks.Task<bool> RefreshPromotionsAsync()
    {
        long request = ++_pricingRequest;
        if (Items.Count == 0) { PricingError = string.Empty; IsPricing = false; return true; }
        if (PromotionService is null) { IsPricing = false; return true; }
        long revision = _revision;
        var snapshot = Items.Select(i => new CartItemDto(i.Id, i.Name, i.OriginalUnitPrice, i.Quantity, i.Unit)
        { Category = i.Category }).ToList();
        try
        {
            IsPricing = true;
            var evaluated = await PromotionService.EvaluateAndApplyPromotionsAsync(snapshot);
            if (revision != _revision || request != _pricingRequest) return false;
            _applyingPromotions = true;
            foreach (var result in evaluated)
            {
                var item = Items.Single(i => i.Id == result.Id);
                item.AppliedPromotionId = result.AppliedPromotionId;
                item.AppliedDiscountAmount = result.AppliedDiscountAmount;
                item.PromotionDescription = result.PromotionDescription;
            }
            PricingError = string.Empty;
            RecalculateTotals();
            return true;
        }
        catch (System.Exception ex) { if (revision == _revision && request == _pricingRequest) PricingError = ex.Message; return false; }
        finally { _applyingPromotions = false; if (request == _pricingRequest) IsPricing = false; }
    }
}
