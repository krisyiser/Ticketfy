using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Services;
using Ticketfy.Services.Audit;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Dialogs;
using System.Linq;
using System;

namespace Ticketfy.ViewModels.Navigation;

/// <summary>
/// Wires all cross-module dialog events in one isolated class.
/// Subscribes to module VM events and delegates to DialogCoordinator.
/// This eliminates the ~250 lines of inline event handlers from MainWindowViewModel's constructor.
/// </summary>
public sealed class DialogWirer
{
    private readonly DialogCoordinator _dialogs;
    private readonly IProductService _productService;
    private readonly ICustomerService _customerService;
    private readonly ISaleService _saleService;
    private readonly IEscPosPrinterService _printerService;
    private readonly IPrintDispatcherService _printDispatcherService;
    private readonly IGiftcardService _giftcardService;
    private readonly IShiftService _shiftService;
    private readonly ISessionManager _sessionManager;
    private readonly IUserRepository _userRepository;
    private readonly IBackupService _backupService;
    private readonly IAttendanceService _attendanceService;
    private readonly IAuditService _auditService;
    private readonly MercadoPagoTerminalService _terminalService;
    private readonly ItemKitService _kitService;
    private readonly ShiftNoteService _shiftNoteService;
    private readonly CommercialReconciliationService _reconciliation;
    private readonly CategoryService _categories;

    public DialogWirer(
        DialogCoordinator dialogs,
        IProductService productService,
        ICustomerService customerService,
        ISaleService saleService,
        IEscPosPrinterService printerService,
        IPrintDispatcherService printDispatcherService,
        IGiftcardService giftcardService,
        IShiftService shiftService,
        ISessionManager sessionManager,
        IUserRepository userRepository,
        IBackupService backupService,
        IAttendanceService attendanceService,
        IAuditService auditService,
        MercadoPagoTerminalService terminalService,
        ItemKitService kitService,
        ShiftNoteService shiftNoteService,
        CommercialReconciliationService reconciliation, CategoryService categories)
    {
        _dialogs = dialogs;
        _reconciliation = reconciliation;
        _categories = categories;
        _productService = productService;
        _customerService = customerService;
        _saleService = saleService;
        _printerService = printerService;
        _printDispatcherService = printDispatcherService;
        _giftcardService = giftcardService;
        _shiftService = shiftService;
        _sessionManager = sessionManager;
        _userRepository = userRepository;
        _backupService = backupService;
        _attendanceService = attendanceService;
        _auditService = auditService;
        _terminalService = terminalService;
        _kitService = kitService;
        _shiftNoteService = shiftNoteService;
    }

    public void WirePosVm(PosViewModel posVm, InventoryViewModel inventoryVm, HistoryViewModel historyVm)
    {
        posVm.OpenCheckoutRequested += () =>
        {
            CheckoutDialogViewModel? dialog = null;
            dialog = new CheckoutDialogViewModel(
                _saleService, _customerService, _printDispatcherService, _terminalService,
                posVm.Cart.CartState.Items.ToList(), posVm.Cart.CartState.Total,
                async () =>
                {
                    if (!_dialogs.IsCurrent(dialog)) return;
                    posVm.Cart.ClearCartCommand.Execute(null);
                    await posVm.LoadProductsAsync();
                    if (!_dialogs.IsCurrent(dialog)) return;
                    await posVm.Cart.LoadCustomersAsync();
                    if (!_dialogs.IsCurrent(dialog)) return;
                    await historyVm.LoadSalesAsync();
                    if (!_dialogs.IsCurrent(dialog)) return;
                    await historyVm.LoadCashierPerformanceAsync();
                },
                _giftcardService,
                preselectedCustomer: posVm.Cart.SelectedCustomer, reconciliation: _reconciliation);

            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_dialogs.IsCurrent(dialog)) return;
                dialog.PaymentMethod = posVm.Cart.InitialPaymentMode;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };

        posVm.OpenAddCustomerRequested += () =>
        {
            var dialog = new CustomerDialogViewModel(_customerService);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = posVm.Cart.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        posVm.Catalog.OpenProductDialogWithParamsRequested += (parameters) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _sessionManager);
            dialog.LoadFromParameters(parameters);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenShiftNotesRequested += () =>
        {
            var dialog = new ShiftNotesDialogViewModel(_shiftNoteService);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = posVm.Header.LoadActiveShiftNotesAsync();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenSwitchUserPinRequested += () =>
        {
            SwitchUserPinDialogViewModel? dialog = null;
            dialog = new SwitchUserPinDialogViewModel(_userRepository, _sessionManager, () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            });
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenLockScreenRequested += () =>
        {
            _sessionManager.LockTerminal();
        };

        posVm.OpenSupervisorPinRequested += (title, callback) =>
        {
            SupervisorPinDialogViewModel? dialog = null;
            dialog = new SupervisorPinDialogViewModel(_userRepository, title, (authorized, user) =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) { callback?.Invoke(false); return; }
                callback?.Invoke(authorized);
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            });
            _dialogs.ShowDialog(dialog);
        };

        posVm.Catalog.OpenBulkWeightRequested += (product) =>
        {
            var dialog = new BulkWeightDialogViewModel(product);
            dialog.RequestCloseWithResult += (confirmed, quantity) =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                if (confirmed && quantity > 0)
                {
                    posVm.Catalog.DirectAddToCart(product, quantity);
                }
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WireInventoryVm(InventoryViewModel inventoryVm, PosViewModel posVm)
    {
        inventoryVm.OpenAddProductRequested += () =>
        {
            var dialog = new ProductDialogViewModel(_productService, _sessionManager);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenProductDialogWithParamsRequested += (parameters) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _sessionManager);
            dialog.LoadFromParameters(parameters);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenEditProductRequested += (product) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _sessionManager);
            dialog.LoadProductForEdit(product);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenConfigureLowStockRequested += () =>
        {
            double currentVal = inventoryVm.DefaultMinStock;
            var dialog = new ConfigureMinStockDialogViewModel(currentVal, inventoryVm.SaveDefaultMinStockAsync);
            dialog.Saved += (newVal) =>
            {
                if (!_dialogs.IsCurrent(dialog)) return;
                inventoryVm.ShowOnlyLowStock = true;
                _ = inventoryVm.LoadProductsAsync();
            };
            dialog.RequestClose += () => _dialogs.TryCloseDialog(dialog);
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenManageCategoriesRequested += () =>
        {
            var dialog = new ManageCategoriesDialogViewModel(_categories);
            dialog.CategoriesUpdated += () =>
            {
                if (!_dialogs.IsCurrent(dialog)) return;
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            dialog.RequestClose += () => _dialogs.TryCloseDialog(dialog);
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WireCustomersVm(CustomersViewModel customersVm, PosViewModel posVm)
    {
        customersVm.OpenAddCustomerRequested += () =>
        {
            var dialog = new CustomerDialogViewModel(_customerService);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = customersVm.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        customersVm.OpenEditCustomerRequested += (customer) =>
        {
            var dialog = new CustomerDialogViewModel(_customerService);
            dialog.LoadForEdit(customer);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = customersVm.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        customersVm.OpenAddPaymentRequested += (customer) =>
        {
            var dialog = new PaymentDialogViewModel(_customerService, customer.Id, customer.Debt);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = customersVm.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        customersVm.OpenStatementRequested += (customer) =>
        {
            var dialog = new CustomerStatementDialogViewModel(_customerService, customer.Id, customer.Name, customer.Rfc, customer.Debt);
            dialog.RequestClose += () => _dialogs.TryCloseDialog(dialog);
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WireHistoryVm(HistoryViewModel historyVm, PosViewModel posVm)
    {
        historyVm.OpenCashupRequested += () =>
        {
            var dialog = new CashupDialogViewModel(_shiftService, _sessionManager, _printerService,
                _backupService, attendanceService: _attendanceService);
            dialog.RequestClose += () => _dialogs.TryCloseDialog(dialog);
            _dialogs.ShowDialog(dialog);
        };

        historyVm.OpenReturnRequested += (sale) =>
        {
            var dialog = new ReturnDialogViewModel(_saleService, sale);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = historyVm.LoadSalesAsync();
                _ = historyVm.LoadCashierPerformanceAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        historyVm.OpenSupervisorPinRequested += (title, callback) =>
        {
            SupervisorPinDialogViewModel? dialog = null;
            dialog = new SupervisorPinDialogViewModel(_userRepository, title, (authorized, user) =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) { callback?.Invoke(false); return; }
                callback?.Invoke(authorized);
            });
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WirePromotionsVm(PromotionsViewModel promotionsVm, PosViewModel posVm, IPromotionService promotionService)
    {
        promotionsVm.OpenEditPromotionRequested += promotion =>
        {
            var dialog = new PromotionDialogViewModel(promotionService, _productService, promotion);
            dialog.RequestClose += () => { if (!_dialogs.TryCloseDialog(dialog)) return; _ = promotionsVm.LoadPromotionsAsync(); };
            _dialogs.ShowDialog(dialog);
        };
        promotionsVm.OpenCreatePromotionRequested += () =>
        {
            var dialog = new PromotionDialogViewModel(promotionService, _productService);
            dialog.RequestClose += () => { if (!_dialogs.TryCloseDialog(dialog)) return; _ = promotionsVm.LoadPromotionsAsync(); };
            _dialogs.ShowDialog(dialog);
        };
        void OpenKit(Ticketfy.Data.Dtos.KitDraft? draft)
        {
            var dialog = new ItemKitDialogViewModel(_kitService, _productService, draft);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = posVm.LoadProductsAsync();
                _ = promotionsVm.LoadPromotionsAsync();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        }
        promotionsVm.OpenCreateItemKitRequested += () => OpenKit(null);
        if (promotionsVm.Kits is not null)
        {
            promotionsVm.Kits.OpenEditRequested += draft => OpenKit(draft);
            promotionsVm.Kits.CatalogChanged += () => _ = posVm.LoadProductsAsync();
        }
    }

    public void WireExpensesVm(ExpensesViewModel expensesVm)
    {
        expensesVm.OpenCashupRequested += () =>
        {
            var dialog = new CashupDialogViewModel(_shiftService, _sessionManager, _printerService,
                _backupService, attendanceService: _attendanceService);
            dialog.RequestClose += () =>
            {
                if (!_dialogs.TryCloseDialog(dialog)) return;
                _ = expensesVm.LoadExpensesAsync();
            };
            _dialogs.ShowDialog(dialog);
        };
    }
}
