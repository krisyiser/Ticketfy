using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

/// <summary>
/// Core ViewModel for the checkout dialog.
/// Decomposed into partial classes: CheckoutDialogViewModel (Core), CheckoutDialogViewModel.Payments, CheckoutDialogViewModel.Mixed.
/// </summary>
public partial class CheckoutDialogViewModel : ObservableObject
{
    private readonly ISaleService _saleService;
    private readonly ICustomerService _customerService;
    private readonly IPrintDispatcherService _printDispatcher;
    private readonly IPaymentTerminalService _terminalService;
    private readonly IGiftcardService? _giftcardService;
    private readonly List<CartItemDto> _cartItems = [];
    private readonly Func<Task>? _onSuccessCallback;
    private string _checkoutOperationId = Guid.NewGuid().ToString("N");

    [ObservableProperty] private bool _isWaitingForTerminal;
    [ObservableProperty] private string _terminalStatusMessage = string.Empty;
    private CancellationTokenSource? _paymentCts;

    public ObservableCollection<TenderEntryModel> AppliedTenders { get; } = new();

    public decimal TotalApplied => AppliedTenders.Sum(t => t.AmountPaid);
    public decimal RemainingBalance => Math.Max(0m, TotalToPay - TotalApplied);
    public bool IsFullyPaid => (TotalApplied >= TotalToPay || ReceivedAmount >= TotalToPay) && TotalToPay > 0;

    [ObservableProperty] private decimal _totalToPay;
    [ObservableProperty] private decimal _receivedAmount;
    [ObservableProperty] private string _receivedAmountInput = "0";

    [ObservableProperty]
    private bool _shouldPrintReceipt = true;

    [ObservableProperty] private decimal _paidAmount;
    [ObservableProperty] private decimal _changeAmount;
    [ObservableProperty] private string _paymentMethod = "Efectivo";

    [ObservableProperty] private decimal _cashAmount;
    [ObservableProperty] private decimal _cardAmount;
    [ObservableProperty] private decimal _walletAmount;
    [ObservableProperty] private decimal _totalBill;

    public bool IsCashPayment => PaymentMethod == "Efectivo";
    public bool IsMixedPayment => PaymentMethod == "Mixto";
    public bool IsNotMixedPayment => PaymentMethod != "Mixto";
    public decimal TotalMixedReceived => CashAmount + CardAmount + WalletAmount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmPaymentCommand))]
    private bool _isProcessing;

    public bool IsSufficientAmount
    {
        get
        {
            if (PaymentMethod == "Mixto")
            {
                return (CashAmount + CardAmount + WalletAmount) >= TotalBill && string.IsNullOrEmpty(ErrorMessage);
            }
            return IsFullyPaid || ReceivedAmount >= TotalToPay || PaymentMethod == "Monedero / Tarjeta de Regalo" || IsCreditPayment;
        }
    }

    private bool CanConfirmPayment()
    {
        if (IsProcessing) return false;

        if (PaymentMethod == "Mixto")
        {
            return (CashAmount + CardAmount + WalletAmount) >= TotalBill && string.IsNullOrEmpty(ErrorMessage);
        }

        if (IsCreditPayment)
        {
            return SelectedCustomer != null && !CreditIsInsufficient;
        }

        return IsSufficientAmount;
    }

    public string ChangeOrShortageText
    {
        get
        {
            if (PaymentMethod == "Mixto")
            {
                decimal totalMixed = CashAmount + CardAmount + WalletAmount;
                if (totalMixed < TotalBill)
                {
                    return $"Faltante: ${(TotalBill - totalMixed):N2}";
                }
                return $"Cambio: ${ChangeAmount:N2}";
            }
            if (string.IsNullOrWhiteSpace(ReceivedAmountInput) || !decimal.TryParse(ReceivedAmountInput, out _))
            {
                return "Monto Inválido";
            }
            decimal effectivePaid = Math.Max(ReceivedAmount, TotalApplied);
            return effectivePaid < TotalToPay
                ? $"Faltante: ${(TotalToPay - effectivePaid):N2}"
                : $"Cambio: ${(effectivePaid - TotalToPay):N2}";
        }
    }

    public string ChangeTextColor => IsSufficientAmount ? "#10B981" : "#EF4444";
    public string ChangeBgColor => IsSufficientAmount ? "#ECFDF5" : "#FEF2F2";

    // Giftcard / Monedero
    [ObservableProperty] private string _giftcardNumber = string.Empty;

    // Direct CFDI 4.0 Invoicing Fields
    [ObservableProperty] private bool _requiresInvoice = false;
    [ObservableProperty] private string _fiscalRfc = "XAXX010101000";
    [ObservableProperty] private string _fiscalRazonSocial = "PÚBLICO EN GENERAL";
    [ObservableProperty] private string _fiscalEmail = string.Empty;
    [ObservableProperty] private string _fiscalUsoCfdi = "G01 - Gastos en General";
    [ObservableProperty] private string _fiscalZipCode = string.Empty;
    [ObservableProperty] private string _fiscalRegime = "616 - Sin obligaciones fiscales";

    public ObservableCollection<CustomerDto> Customers { get; } = [];
    [ObservableProperty] private CustomerDto? _selectedCustomer;
    [ObservableProperty] private double _customerPointsBalance = 0.0;
    [ObservableProperty] private double _pointsEarnedThisSale = 0.0;

    public ObservableCollection<string> PaymentMethods { get; } = [
        "Efectivo", "Tarjeta Débito/Crédito", "Transferencia SPEI", "Mixto",
        "Crédito de Cliente", "Puntos de Fidelidad", "Monedero / Tarjeta de Regalo", "CoDi / QR"
    ];

    public bool IsCreditPayment =>
        PaymentMethod == "Crédito de Cliente" ||
        PaymentMethod == "Crédito / Cuenta Corriente" ||
        PaymentMethod == "Crédito" ||
        PaymentMethod == "Credit" ||
        PaymentMethod == "Fiado";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreditAvailableDisplay))]
    [NotifyPropertyChangedFor(nameof(CreditIsInsufficient))]
    private decimal _customerCreditLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreditAvailableDisplay))]
    [NotifyPropertyChangedFor(nameof(CreditIsInsufficient))]
    private decimal _customerCurrentDebt;

    public decimal AvailableCredit => Math.Max(0m, CustomerCreditLimit - CustomerCurrentDebt);

    public string CreditAvailableDisplay => $"Crédito disponible: ${AvailableCredit:N2}  |  Límite: ${CustomerCreditLimit:N2}  |  Deuda actual: ${CustomerCurrentDebt:N2}";

    public bool CreditIsInsufficient => IsCreditPayment && AvailableCredit < TotalToPay;

    public ObservableCollection<string> UsoCfdiOptions { get; } = [
        "G01 - Gastos en General", "G03 - Gastos en general", "I03 - Equipo de Transporte", "I04 - Equipo de Cómputo", "P01 - Por Definir", "S01 - Sin efectos fiscales"
    ];

    public ObservableCollection<string> RegimenFiscalOptions { get; } = [
        "616 - Sin obligaciones fiscales", "601 - General de Ley Personas Morales", "605 - Sueldos y Salarios", "606 - Arrendamiento", "612 - Personas Físicas con Actividades Empresariales", "626 - RESICO"
    ];

    [ObservableProperty] private string _errorMessage = string.Empty;

    public event Action? RequestClose;

    public CheckoutDialogViewModel(
        ISaleService saleService,
        ICustomerService customerService,
        IPrintDispatcherService printDispatcher,
        IPaymentTerminalService terminalService,
        List<CartItemDto> cartItems,
        decimal total,
        Func<Task>? onSuccessCallback = null,
        IGiftcardService? giftcardService = null,
        CustomerDto? preselectedCustomer = null)
    {
        _saleService = saleService;
        _customerService = customerService;
        _printDispatcher = printDispatcher;
        _terminalService = terminalService;
        _giftcardService = giftcardService;
        _cartItems = cartItems ?? [];
        _onSuccessCallback = onSuccessCallback;

        TotalToPay = total;
        TotalBill = total;
        ReceivedAmount = 0m;
        PaidAmount = 0m;
        ReceivedAmountInput = "0";

        PointsEarnedThisSale = _cartItems != null ? _cartItems.Sum(i => i.PointsRewarded * i.Quantity) : 0.0;

        _ = LoadCustomersAsync(preselectedCustomer);
    }

    public bool HasPointsEarnedThisSale => PointsEarnedThisSale > 0;

    private async Task LoadCustomersAsync(CustomerDto? preselected = null)
    {
        try
        {
            var list = await _customerService.GetAllAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Customers.Clear();
                foreach (var c in list) Customers.Add(c);

                if (preselected != null)
                {
                    SelectedCustomer = Customers.FirstOrDefault(c => c.Id == preselected.Id) ?? preselected;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customers in CheckoutDialog");
        }
    }

    partial void OnSelectedCustomerChanged(CustomerDto? value)
    {
        if (value != null)
        {
            CustomerPointsBalance = value.PuntosSaldo;
            CustomerCreditLimit = value.CreditLimit;
            CustomerCurrentDebt = value.Debt;
            FiscalRazonSocial = value.Nombre;
            if (!string.IsNullOrWhiteSpace(value.Rfc)) FiscalRfc = value.Rfc;
            if (!string.IsNullOrWhiteSpace(value.Email)) FiscalEmail = value.Email;
        }
        else
        {
            CustomerCreditLimit = 0m;
            CustomerCurrentDebt = 0m;
        }

        OnPropertyChanged(nameof(AvailableCredit));
        OnPropertyChanged(nameof(CreditAvailableDisplay));
        OnPropertyChanged(nameof(CreditIsInsufficient));
        OnPropertyChanged(nameof(IsCreditPayment));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }
}
