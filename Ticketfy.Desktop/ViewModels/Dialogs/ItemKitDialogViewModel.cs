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

namespace Ticketfy.ViewModels.Dialogs;

public partial class ItemKitDialogViewModel : ObservableObject
{
    private readonly IItemKitService _kitService;
    private readonly IProductService _productService;

    [ObservableProperty] private string _kitBarcode = string.Empty;
    [ObservableProperty] private string _kitName = string.Empty;
    [ObservableProperty] private decimal _kitPrice;
    [ObservableProperty] private string _kitDescription = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public ObservableCollection<ProductDto> AvailableProducts { get; } = [];
    public ObservableCollection<ItemKitItemDto> DraftKitItems { get; } = [];

    [ObservableProperty] private ProductDto? _selectedProduct;
    [ObservableProperty] private double _itemQuantity = 1.0;

    public event Action? RequestClose;

    public ItemKitDialogViewModel(IItemKitService kitService, IProductService productService)
    {
        _kitService = kitService;
        _productService = productService;
        _ = LoadProductsAsync();
    }

    private async Task LoadProductsAsync()
    {
        try
        {
            var list = await _productService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableProducts.Clear();
                foreach (var p in list) AvailableProducts.Add(p);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading products for ItemKit dialog");
        }
    }

    [RelayCommand]
    private void AddItemToDraft()
    {
        if (SelectedProduct == null || ItemQuantity <= 0) return;

        var existing = DraftKitItems.FirstOrDefault(i => i.ProductId == SelectedProduct.Id);
        if (existing != null)
        {
            DraftKitItems.Remove(existing);
            DraftKitItems.Add(existing with { Quantity = existing.Quantity + ItemQuantity });
        }
        else
        {
            DraftKitItems.Add(new ItemKitItemDto(
                Guid.NewGuid().ToString(),
                string.Empty,
                SelectedProduct.Id,
                SelectedProduct.Name,
                ItemQuantity
            ));
        }

        ItemQuantity = 1.0;
    }

    [RelayCommand]
    private void RemoveItemFromDraft(ItemKitItemDto item)
    {
        if (item != null) DraftKitItems.Remove(item);
    }

    [RelayCommand]
    private async Task SaveKitAsync()
    {
        if (string.IsNullOrWhiteSpace(KitName))
        {
            FeedbackMessage = "El nombre del combo / paquete es obligatorio";
            return;
        }

        if (KitPrice <= 0)
        {
            FeedbackMessage = "Ingrese un precio mayor a $0.00 MXN";
            return;
        }

        if (DraftKitItems.Count == 0)
        {
            FeedbackMessage = "Debe agregar al menos 1 producto ingrediente al combo";
            return;
        }

        if (string.IsNullOrWhiteSpace(KitBarcode))
        {
            KitBarcode = $"750{Random.Shared.Next(100000000, 999999999)}";
        }

        try
        {
            var success = await _kitService.SaveAsync(
                Guid.NewGuid().ToString(),
                KitBarcode.Trim(),
                KitName.Trim(),
                KitPrice,
                KitDescription,
                DraftKitItems.ToList()
            );

            if (success)
            {
                FeedbackMessage = string.Empty;
                RequestClose?.Invoke();
            }
            else
            {
                FeedbackMessage = "Error guardando el combo / paquete. Verifique que el código no esté duplicado.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving ItemKit");
            FeedbackMessage = "Error al guardar combo / paquete";
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}
