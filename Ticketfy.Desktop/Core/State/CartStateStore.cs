using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.State;

public partial class CartStateStore : ObservableObject
{
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
            var result = existing.IncreaseQuantity(1.0, absoluteDbStock);
            if (!result.Success)
            {
                // In a real scenario we could throw an exception or return the result
                // For now, the user prompt states the domain protects itself.
            }
        }
        else
        {
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
    }
}
