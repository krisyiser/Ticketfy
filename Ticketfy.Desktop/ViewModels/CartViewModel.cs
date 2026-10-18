using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.State;
using Ticketfy.Core.Repositories;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;

namespace Ticketfy.ViewModels;

public partial class CartViewModel : ObservableObject
{
    public CartStateStore CartState { get; }

    private readonly ISaleService _saleService;
    private readonly ICustomerService _customerService;

    public ObservableCollection<CustomerDto> Customers { get; } = [];
    public ObservableCollection<ParkedTicketModel> ParkedTickets { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomerCredit))]
    [NotifyPropertyChangedFor(nameof(CustomerCreditBadgeText))]
    [NotifyPropertyChangedFor(nameof(CustomerCreditBadgeColor))]
    private CustomerDto? _selectedCustomer;
    [ObservableProperty] private int _parkedOrdersCount = 0;
    [ObservableProperty] private double _cartWidthPx = 380;
    [ObservableProperty] private string _initialPaymentMode = "Efectivo";

    public bool HasCustomerCredit => SelectedCustomer?.CreditLimit > 0;
    public string CustomerCreditBadgeText => HasCustomerCredit ? $"CRÉDITO: {SelectedCustomer!.AvailableCredit:C}" : string.Empty;
    public string CustomerCreditBadgeColor => SelectedCustomer?.AvailableCredit > 0 ? "#10B981" : "#EF4444";

    public bool HasParkedTickets => ParkedTickets.Count > 0;

    public event Action? OpenCheckoutRequested;
    public event Action? OpenAddCustomerRequested;

    public CartViewModel(CartStateStore cartState, ISaleService saleService, ICustomerService customerService)
    {
        CartState = cartState;
        _saleService = saleService;
        _customerService = customerService;

        _ = LoadCustomersAsync();
    }

    public async Task LoadCustomersAsync()
    {
        try
        {
            var list = await _customerService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var publicoGeneral = new CustomerDto("", "Público General", "", "", "", 0m, 0m, 0.0, 0m, "");
                Customers.Clear();
                Customers.Add(publicoGeneral);
                foreach (var c in list) Customers.Add(c);

                if (SelectedCustomer == null)
                {
                    SelectedCustomer = publicoGeneral;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customers in CartViewModel");
        }
    }

    [RelayCommand]
    private void OpenCustomerSelect()
    {
        OpenAddCustomerRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CheckoutAsync(string method)
    {
        await CartState.RefreshPromotionsAsync();
        if (!string.IsNullOrEmpty(CartState.PricingError)) return;
        InitialPaymentMode = method ?? "Efectivo";
        OpenCheckoutRequested?.Invoke();
    }

    [RelayCommand]
    private void IncreaseQuantity(CartItemDto item)
    {
        var result = item.IncreaseQuantity(1.0, item.AvailableStock);
        if (result.Success) CartState.RecalculateTotals();
    }

    [RelayCommand]
    private void DecreaseQuantity(CartItemDto item)
    {
        if (item.Quantity > 1)
        {
            item.DecreaseQuantity(1.0);
            CartState.RecalculateTotals();
        }
        else
        {
            CartState.RemoveItem(item);
        }
    }

    [RelayCommand]
    private void RemoveFromCart(CartItemDto item)
    {
        CartState.RemoveItem(item);
    }

    [RelayCommand]
    private void ClearCart()
    {
        CartState.Clear();
        SelectedCustomer = Customers.FirstOrDefault(c => string.IsNullOrEmpty(c.Id));
    }
}
