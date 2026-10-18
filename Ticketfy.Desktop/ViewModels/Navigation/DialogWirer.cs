using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Data;
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
    private readonly AppDbContext _db;
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
        AppDbContext db,
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
        _db = db;
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
            var dialog = new CheckoutDialogViewModel(
                _saleService, _customerService, _printDispatcherService, _terminalService,
                posVm.Cart.CartState.Items.ToList(), posVm.Cart.CartState.Total,
                async () =>
                {
                    posVm.Cart.ClearCartCommand.Execute(null);
                    await posVm.LoadProductsAsync();
                    await posVm.Cart.LoadCustomersAsync();
                    await historyVm.LoadSalesAsync();
                    await historyVm.LoadCashierPerformanceAsync();
                },
                _giftcardService,
                preselectedCustomer: posVm.Cart.SelectedCustomer, reconciliation: _reconciliation);

            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                dialog.PaymentMethod = posVm.Cart.InitialPaymentMode;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };

        posVm.OpenAddCustomerRequested += () =>
        {
            var dialog = new CustomerDialogViewModel(_customerService);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = posVm.Cart.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        posVm.Catalog.OpenProductDialogWithParamsRequested += (parameters) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _db, _sessionManager, _auditService);
            dialog.LoadFromParameters(parameters);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
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
                _dialogs.CloseDialog();
                _ = posVm.Header.LoadActiveShiftNotesAsync();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenSwitchUserPinRequested += () =>
        {
            var dialog = new SwitchUserPinDialogViewModel(_userRepository, _sessionManager, () =>
            {
                _dialogs.CloseDialog();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            });
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenLockScreenRequested += () =>
        {
            var dialog = new LockScreenDialogViewModel(_userRepository, _sessionManager, () =>
            {
                _dialogs.CloseDialog();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            });
            _dialogs.ShowDialog(dialog);
        };

        posVm.OpenSupervisorPinRequested += (title, callback) =>
        {
            var dialog = new SupervisorPinDialogViewModel(_userRepository, title, (authorized, user) =>
            {
                _dialogs.CloseDialog();
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
                _dialogs.CloseDialog();
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
            var dialog = new ProductDialogViewModel(_productService, _db, _sessionManager, _auditService);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenProductDialogWithParamsRequested += (parameters) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _db, _sessionManager, _auditService);
            dialog.LoadFromParameters(parameters);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenEditProductRequested += (product) =>
        {
            var dialog = new ProductDialogViewModel(_productService, _db, _sessionManager, _auditService);
            dialog.LoadProductForEdit(product);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenConfigureLowStockRequested += () =>
        {
            double currentVal = InventoryViewModel.LoadDefaultMinStock();
            var dialog = new ConfigureMinStockDialogViewModel(currentVal);
            dialog.Saved += (newVal) =>
            {
                InventoryViewModel.SaveDefaultMinStock(newVal);
                inventoryVm.ShowOnlyLowStock = true;
                _ = inventoryVm.LoadProductsAsync();
            };
            dialog.RequestClose += _dialogs.CloseDialog;
            _dialogs.ShowDialog(dialog);
        };

        inventoryVm.OpenManageCategoriesRequested += () =>
        {
            var dialog = new ManageCategoriesDialogViewModel(_categories);
            dialog.CategoriesUpdated += () =>
            {
                _ = inventoryVm.LoadProductsAsync();
                _ = posVm.LoadProductsAsync();
            };
            dialog.RequestClose += _dialogs.CloseDialog;
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
                _dialogs.CloseDialog();
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
                _dialogs.CloseDialog();
                _ = customersVm.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        customersVm.OpenAddPaymentRequested += (customer) =>
        {
            var dialog = new PaymentDialogViewModel(_customerService, customer.Id, customer.Debt);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = customersVm.LoadCustomersAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        customersVm.OpenStatementRequested += (customer) =>
        {
            var dialog = new CustomerStatementDialogViewModel(_customerService, customer.Id, customer.Name, customer.Rfc, customer.Debt);
            dialog.RequestClose += _dialogs.CloseDialog;
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WireHistoryVm(HistoryViewModel historyVm, PosViewModel posVm)
    {
        historyVm.OpenCashupRequested += () =>
        {
            var dialog = new CashupDialogViewModel(_db, _shiftService, _sessionManager, _printerService,
                _backupService, attendanceService: _attendanceService);
            dialog.RequestClose += _dialogs.CloseDialog;
            _dialogs.ShowDialog(dialog);
        };

        historyVm.OpenReturnRequested += (sale) =>
        {
            var dialog = new ReturnDialogViewModel(_saleService, sale);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = historyVm.LoadSalesAsync();
                _ = historyVm.LoadCashierPerformanceAsync();
                _ = posVm.LoadProductsAsync();
            };
            _dialogs.ShowDialog(dialog);
        };

        historyVm.OpenSupervisorPinRequested += (title, callback) =>
        {
            var dialog = new SupervisorPinDialogViewModel(_userRepository, title, (authorized, user) =>
            {
                _dialogs.CloseDialog();
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
            dialog.RequestClose += () => { _dialogs.CloseDialog(); _ = promotionsVm.LoadPromotionsAsync(); };
            _dialogs.ShowDialog(dialog);
        };
        promotionsVm.OpenCreatePromotionRequested += () =>
        {
            var dialog = new PromotionDialogViewModel(promotionService, _productService);
            dialog.RequestClose += () => { _dialogs.CloseDialog(); _ = promotionsVm.LoadPromotionsAsync(); };
            _dialogs.ShowDialog(dialog);
        };
        promotionsVm.OpenCreateItemKitRequested += () =>
        {
            var dialog = new ItemKitDialogViewModel(_kitService, _productService);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = posVm.LoadProductsAsync();
                _ = promotionsVm.LoadPromotionsAsync();
                WeakReferenceMessenger.Default.Send(new FocusSearchMessage());
            };
            _dialogs.ShowDialog(dialog);
        };
    }

    public void WireExpensesVm(ExpensesViewModel expensesVm)
    {
        expensesVm.OpenCashupRequested += () =>
        {
            var dialog = new CashupDialogViewModel(_db, _shiftService, _sessionManager, _printerService,
                _backupService, attendanceService: _attendanceService);
            dialog.RequestClose += () =>
            {
                _dialogs.CloseDialog();
                _ = expensesVm.LoadExpensesAsync();
            };
            _dialogs.ShowDialog(dialog);
        };
    }
}
