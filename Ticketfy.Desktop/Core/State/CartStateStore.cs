using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.State;

public partial class CartStateStore : ObservableObject
{
    public Ticketfy.Services.Interfaces.IPromotionService? PromotionService { get; set; }
    private long _revision;
    private bool _applyingPromotions;
    public string PricingError { get; private set; } = string.Empty;
    public ObservableCollection<CartItemDto> Items { get; } = new();

    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _total;

    public event System.Action<string>? ProductAddedToCart;

    public void AddItem(CartItemDto item, double absoluteDbStock)
    {
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
        RecalculateTotals();
    }

    public void RecalculateTotals()
    {
        Subtotal = Items.Sum(i => i.GetLineTotal());
        Total = Subtotal; // Assuming tax logic is handled elsewhere or later
        if (!_applyingPromotions)
        {
            _revision++;
            _ = RefreshPromotionsAsync();
        }
    }

    public async System.Threading.Tasks.Task RefreshPromotionsAsync()
    {
        if (PromotionService is null) return;
        long revision = _revision;
        var snapshot = Items.Select(i => new CartItemDto(i.Id, i.Name, i.OriginalUnitPrice, i.Quantity, i.Unit)
        { Category = i.Category }).ToList();
        try
        {
            var evaluated = await PromotionService.EvaluateAndApplyPromotionsAsync(snapshot);
            if (revision != _revision) return;
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
        }
        catch (System.Exception ex) { PricingError = ex.Message; }
        finally { _applyingPromotions = false; }
    }
}
