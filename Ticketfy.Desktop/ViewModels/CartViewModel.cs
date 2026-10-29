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
    private long _customerRequestVersion;
    private long _checkoutVersion;
    private bool _preparingCheckout;
    private readonly Ticketfy.Core.Services.ISessionManager? _session;
    [ObservableProperty] private string _checkoutMessage = string.Empty;
    [ObservableProperty] private string _customerSearch = string.Empty;
    [ObservableProperty] private string _customerLoadMessage = string.Empty;
    [ObservableProperty] private bool _needsCustomerSearch;

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

    public CartViewModel(CartStateStore cartState, ISaleService saleService, ICustomerService customerService, bool loadOnCreate = true,
        Ticketfy.Core.Services.ISessionManager? session = null)
    {
        CartState = cartState;
        _saleService = saleService;
        _customerService = customerService;
        _session = session;

        if (loadOnCreate) _ = LoadCustomersAsync();
    }

    public async Task LoadCustomersAsync(CustomerDto? selectCustomer = null)
    {
        var version = ++_customerRequestVersion;
        if (_customerService is null) return;
        try
        {
            var list = await _customerService.SearchAsync(CustomerSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _customerRequestVersion) return;
                var selected = selectCustomer ?? SelectedCustomer;
                var publicoGeneral = new CustomerDto("", "Público General", "", "", "", 0m, 0m, 0.0, 0m, "");
                Customers.Clear();
                Customers.Add(publicoGeneral);
                foreach (var c in list) Customers.Add(c);
                if (selected is not null && !string.IsNullOrEmpty(selected.Id) && Customers.All(c => c.Id != selected.Id))
                    Customers.Add(selected);
                SelectedCustomer = Customers.FirstOrDefault(c => c.Id == selected?.Id) ?? publicoGeneral;
                CustomerLoadMessage = list.Count >= 100 ? "Mostrando hasta 100 coincidencias. Refine la búsqueda." : string.Empty;
                NeedsCustomerSearch = list.Count >= 100 || !string.IsNullOrWhiteSpace(CustomerSearch);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customers in CartViewModel");
            if (version == _customerRequestVersion)
            { CustomerLoadMessage = "No se pudieron consultar los clientes. Vuelva a buscar para reintentar."; NeedsCustomerSearch = true; }
        }
    }

    [RelayCommand]
    private Task SearchCustomersAsync() => LoadCustomersAsync();

    internal void InvalidateCheckoutPreparation()
    {
        ++_checkoutVersion;
        CheckoutMessage = string.Empty;
    }

    public void ClearSessionData()
    {
        InvalidateCheckoutPreparation();
        ++_customerRequestVersion;
        Customers.Clear();
        SelectedCustomer = null;
        ParkedTickets.Clear();
        ParkedOrdersCount = 0;
        CustomerSearch = CustomerLoadMessage = string.Empty;
        NeedsCustomerSearch = false;
        InitialPaymentMode = "Efectivo";
        CartState.Clear();
    }

    [RelayCommand]
    private void OpenCustomerSelect()
    {
        OpenAddCustomerRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CheckoutAsync(string method)
    {
        // The command binding disables concurrent clicks; also guard direct/shortcut invocations.
        if (_preparingCheckout) return;
        _preparingCheckout = true;
        try { await PrepareCheckoutAsync(method); }
        finally { _preparingCheckout = false; }
    }

    private async Task PrepareCheckoutAsync(string method)
    {
        if (_session is not null && (_session.CurrentCashier is null || _session.IsTerminalLocked)) return;
        if (CartState.Items.Count == 0) { CheckoutMessage = "Agregue productos antes de cobrar."; return; }
        long version = _checkoutVersion, revision = CartState.Revision;
        long? sessionVersion = _session?.SessionVersion;
        CheckoutMessage = "Comprobando promociones e importes...";
        bool priced = await CartState.RefreshPromotionsAsync();
        if (version != _checkoutVersion || sessionVersion != _session?.SessionVersion || _session?.IsTerminalLocked == true) return;
        if (!priced || revision != CartState.Revision || CartState.Items.Count == 0 || !string.IsNullOrEmpty(CartState.PricingError))
        {
            CheckoutMessage = string.IsNullOrEmpty(CartState.PricingError)
                ? "El ticket cambió durante la comprobación. Revise sus importes y vuelva a pulsar Cobrar."
                : "No se pudieron comprobar las promociones. Vuelva a pulsar Cobrar para reintentar.";
            return;
        }
        CheckoutMessage = string.Empty;
        InitialPaymentMode = method ?? "Efectivo";
        OpenCheckoutRequested?.Invoke();
    }

    partial void OnSelectedCustomerChanged(CustomerDto? value)
    {
        InvalidateCheckoutPreparation();
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
        InvalidateCheckoutPreparation();
        CartState.Clear();
        SelectedCustomer = Customers.FirstOrDefault(c => string.IsNullOrEmpty(c.Id));
    }
}
