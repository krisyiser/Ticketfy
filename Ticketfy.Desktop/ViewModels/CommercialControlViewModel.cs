using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels;

public sealed record CommercialLedgerRow(string Date, string Document, string Event, decimal? Amount, decimal? Balance,
    string User, string Register, string Shift)
{
    public string AmountDisplay => Amount?.ToString("N3") ?? "No calculado";
    public string BalanceDisplay => Balance?.ToString("N3") ?? "No calculado";
}

public partial class CommercialControlViewModel : ObservableObject
{
    private readonly IProductService _products;
    private readonly ICustomerService _customers;
    private readonly ISupplierService _suppliers;
    private readonly IPurchaseService _purchases;
    private readonly IShiftService _shifts;
    private readonly IGiftcardService _wallets;
    private readonly CommercialReconciliationService _reconciliation;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext _operations;
    private readonly ISaleService? _sales;
    private readonly IPaymentTerminalReconciliation? _terminalReconciliation;
    public Directories.WalletDirectoryViewModel WalletDirectory { get; }
    public void ClearDirectoryData()
    {
        Purchase = null; Purchases.Clear(); PurchaseLines.Clear();
        WalletDirectory.ClearSessionData();
        PendingCheckoutSearch = "";
        ResetPendingCheckoutQuery();
        TransferSearch = "";
        TransferHistoryMode = false;
        ResetTransferQuery();
        CreditRefundSearch = BankRefundSearch = "";
        ResetRefundQuery(true); ResetRefundQuery(false);
        ResetCreditSelection();
        ResetReconciliation();
        Message = Summary = "";
    }
    private readonly Dictionary<string, string> _keys = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _catalogSearch = "";
    [RelayCommand] private Task SearchCatalogsAsync() => RunAsync(LoadCatalogChoicesAsync);
    private async Task LoadCatalogChoicesAsync()
    {
        Product = null; Customer = null; Supplier = null; Purchase = null;
        HistoricalCustomer = null; HistoricalPurchase = null; InvalidateHistoricalPreview();
        HistoricalCustomers.Clear(); HistoricalPurchases.Clear();
        if (CanHistorical)
        {
            foreach (var target in await Historical.GetTargetsAsync(HistoricalBalanceScope.CustomerAccount, CatalogSearch)) HistoricalCustomers.Add(target);
            foreach (var target in await Historical.GetTargetsAsync(HistoricalBalanceScope.SupplierPurchase, CatalogSearch)) HistoricalPurchases.Add(target);
        }
        Products.Clear(); Customers.Clear(); Suppliers.Clear(); Purchases.Clear();
        if (CanInventory) foreach (var p in (await _products.GetPageAsync(0, 100, CatalogSearch)).Items) Products.Add(p);
        if (CanCustomers) foreach (var c in await _customers.SearchAsync(CatalogSearch)) Customers.Add(c);
        if (CanSuppliers)
        {
            foreach (var s in await _suppliers.SearchAsync(CatalogSearch)) Suppliers.Add(s);
            foreach (var p in await _purchases.GetPageAsync(0, 100, CatalogSearch)) Purchases.Add(p);
        }
    }
    [ObservableProperty] private ProductDto? _product;
    [ObservableProperty] private CustomerDto? _customer;
    [ObservableProperty] private SupplierDto? _supplier;
    [ObservableProperty] private PurchaseDto? _purchase;
    [ObservableProperty] private PurchaseItemDto? _purchaseLine;
    [ObservableProperty] private BankRefundHeader? _refund;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _method = "Efectivo";
    [ObservableProperty] private string _walletNumber = "";
    [ObservableProperty] private string _day = Ticketfy.Core.Helpers.BusinessTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private bool _isOutflow = true;
    [ObservableProperty] private bool _externalRefundVerified;
    [ObservableProperty] private bool _canInventory;
    [ObservableProperty] private bool _canCustomers;
    [ObservableProperty] private bool _canSuppliers;
    [ObservableProperty] private bool _canCash;
    [ObservableProperty] private bool _canWallet;
    [ObservableProperty] private bool _canReconcile;
    [ObservableProperty] private bool _canRecoverCheckout;
    [ObservableProperty] private ShiftDto? _legacyShift;
    [ObservableProperty] private bool _legacyCountVerified;
    public ObservableCollection<ShiftDto> LegacyShifts { get; } = [];
    public ObservableCollection<PendingCheckoutDto> PendingCheckouts { get; } = [];
    [ObservableProperty] private PendingCheckoutDto? _pendingCheckout;
    [ObservableProperty] private bool _checkoutFundsVerified;
    [ObservableProperty] private bool _completeCheckout = true;
    [ObservableProperty] private string _checkoutProviderOrderId = "";
    [ObservableProperty] private string _checkoutBankStatus = "";
    public bool CanQueryCheckoutBank => CanRecoverCheckout && CanTransfers && _terminalReconciliation is not null;
    partial void OnCanRecoverCheckoutChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQueryCheckoutBank));
        if (!value) ResetPendingCheckoutQuery();
    }
    partial void OnCanTransfersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQueryCheckoutBank));
        if (!value) { ResetTransferQuery(); ResetRefundQuery(false); }
    }
    partial void OnPendingCheckoutChanged(PendingCheckoutDto? value)
    { CheckoutProviderOrderId = ""; CheckoutBankStatus = ""; CheckoutFundsVerified = false; }
    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<CustomerDto> Customers { get; } = [];
    public ObservableCollection<SupplierDto> Suppliers { get; } = [];
    public ObservableCollection<PurchaseDto> Purchases { get; } = [];
    public ObservableCollection<PurchaseItemDto> PurchaseLines { get; } = [];
    public ObservableCollection<BankRefundHeader> PendingRefunds { get; } = [];
    public ObservableCollection<CommercialLedgerRow> Rows { get; } = [];
    public string[] PaymentMethods { get; } = ["Efectivo", "Transferencia", "Tarjeta"];

    public CommercialControlViewModel(IProductService products, ICustomerService customers, ISupplierService suppliers,
        IPurchaseService purchases, IShiftService shifts, IGiftcardService wallets, CommercialReconciliationService reconciliation,
        IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations, ISaleService? sales = null,
        IPaymentTerminalReconciliation? terminalReconciliation = null)
    {
        _products = products; _customers = customers; _suppliers = suppliers; _purchases = purchases; _shifts = shifts;
        _wallets = wallets; _reconciliation = reconciliation; _factory = factory; _operations = operations;
        _sales = sales;
        _terminalReconciliation = terminalReconciliation;
        WalletDirectory = new(wallets, number => WalletNumber = number);
    }
    partial void OnPurchaseChanged(PurchaseDto? value)
    {
        PurchaseLines.Clear();
        foreach (var line in value?.Items ?? []) PurchaseLines.Add(line);
        PurchaseLine = PurchaseLines.FirstOrDefault();
    }
    [RelayCommand]
    private Task LoadPurchaseDetailAsync() => RunAsync(async () =>
    {
        var selected = Purchase ?? throw new InvalidOperationException("Seleccione una compra.");
        try
        {
            var detail = await _purchases.GetByIdAsync(selected.Id);
            if (!ReferenceEquals(selected, Purchase)) return;
            Purchase = detail;
            Message = detail?.DetailStatus ?? "No se pudo localizar la compra.";
        }
        catch (Exception)
        {
            if (ReferenceEquals(selected, Purchase))
            {
                Purchase = null;
                Message = "No se pudo consultar el desglose. Seleccione la compra y vuelva a consultar.";
            }
        }
    });
    private string Key(string command)
    {
        if (!_keys.TryGetValue(command, out var key)) _keys[command] = key = Guid.NewGuid().ToString("N");
        return key;
    }
    private async Task RunAsync(Func<Task> action, string? command = null, bool keepLedger = false)
    {
        if (IsBusy) return;
        if (!keepLedger) ResetLedgerQuery();
        IsBusy = true; Message = "";
        try
        {
            await action();
            if (command is not null) { _keys.Remove(command); Message = "Operación registrada. El historial se conserva."; }
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task RequireAsync(string permission)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await _operations.RequireAsync(db, permission, false);
    }
    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async () =>
    {
        async Task<bool> CanAsync(string permission)
        {
            try { await RequireAsync(permission); return true; }
            catch (UnauthorizedAccessException) { return false; }
        }
        CanInventory = await CanAsync(PermissionCatalog.InventoryView);
        CanCustomers = await CanAsync(PermissionCatalog.CustomersView);
        CanSuppliers = await CanAsync(PermissionCatalog.SuppliersManage) && await CanAsync(PermissionCatalog.PurchasesManage);
        CanCash = await CanAsync(PermissionCatalog.CashInOut);
        CanWallet = await CanAsync(PermissionCatalog.WalletManage);
        if (CanWallet) await WalletDirectory.LoadAsync();
        else WalletDirectory.ClearSessionData();
        CanReconcile = await CanAsync(PermissionCatalog.ReportsView);
        CanRecoverCheckout = await CanAsync(PermissionCatalog.CheckoutRecover);
        CanSettleCredit = await CanAsync(PermissionCatalog.AccountCreditSettle);
        ResetRefundQuery(true);
        if (CanSettleCredit) await LoadCreditRefundsCoreAsync();
        CanReviewAttendance = await CanAsync(PermissionCatalog.AttendanceManage);
        CanReviewNotes = await CanAsync(PermissionCatalog.ShiftNotesManage);
        HistoricalNotes.Clear(); HistoricalNote = null;
        AttendanceReviews.Clear(); AttendanceReview = null;
        ResetCreditSelection();
        CanHistorical = await CanAsync(PermissionCatalog.HistoryReconcile);
        HistoricalCustomers.Clear(); HistoricalPurchases.Clear(); InvalidateHistoricalPreview();
        HistoricalCustomer = null; HistoricalPurchase = null;
        CanTransfers = await CanAsync(PermissionCatalog.PaymentsReconcile);
        if (CanTransfers) await LoadPendingTransfersAsync();
        await LoadCatalogChoicesAsync();
        ResetRefundQuery(false);
        LegacyShifts.Clear();
        ResetPendingCheckoutQuery();
        if (CanRecoverCheckout && _sales is not null) await LoadPendingCheckoutPageAsync(null, 0);
        try
        {
            if (CanTransfers)
            {
                foreach (var s in await _shifts.GetLegacyOpenAsync()) LegacyShifts.Add(s);
                await LoadRefundPageAsync(false, null, 0);
            }
        }
        catch (UnauthorizedAccessException) { Message = "Su rol no puede conciliar reembolsos bancarios."; }
    });
    [RelayCommand]
    private Task KardexAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.InventoryView);
        if (Product is null) throw new InvalidOperationException("Seleccione un producto.");
        await StartLedgerAsync("Inventory", Product.Id, Product.Name, Product.Unit);
    });
    private void ShowStatement(AccountStatementPageDto statement, string name, bool wallet)
    {
        Rows.Clear(); decimal balance = statement.OpeningBalance;
        foreach (var e in statement.Entries)
        {
            balance += e.Amount;
            Rows.Add(new(e.CreatedAtUtc, e.DocumentId, e.EntryType, e.Amount, balance, e.CashierUserId, e.RegisterId, e.ShiftId ?? "Migración"));
        }
        Summary = $"{name} · Saldo de la consulta: {statement.Balance:C2}. "
            + (wallet ? "Positivo = saldo disponible del monedero. " : "Negativo = saldo a favor. ")
            + LedgerPagePresentation.AccountStatus(statement);
    }
    [RelayCommand]
    private Task CustomerStatementAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.CustomersView);
        if (Customer is null) throw new InvalidOperationException("Seleccione un cliente.");
        await StartLedgerAsync("Customer", Customer.Id, Customer.Name);
    });
    [RelayCommand]
    private Task SupplierStatementAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.SuppliersManage);
        if (Supplier is null) throw new InvalidOperationException("Seleccione un proveedor.");
        await StartLedgerAsync("Supplier", Supplier.Id, Supplier.Name);
    });
    [RelayCommand]
    private Task PaySupplierAsync() => RunAsync(async () =>
    {
        if (Purchase is null) throw new InvalidOperationException("Seleccione la compra a pagar.");
        var purchase = Purchase;
        await _purchases.PayAsync(purchase.Id, Amount, Method, Reference, Key("SUPPLIER_PAY"));
        Amount = 0m;
        // A failed read after the commit is not a failed payment.
        try { await StartLedgerAsync("Supplier", purchase.SupplierId, purchase.SupplierName); }
        catch (Exception) { ResetLedgerQuery(); Summary = "Pago registrado. No se pudo actualizar el estado de cuenta; consúltelo nuevamente sin repetir el pago."; }
        await RefreshPurchaseAfterCommitAsync(purchase);
    }, "SUPPLIER_PAY");
    [RelayCommand]
    private Task ReturnSupplierAsync() => RunAsync(async () =>
    {
        if (PurchaseLine is null) throw new InvalidOperationException("Seleccione la partida de la compra.");
        var purchase = Purchase ?? throw new InvalidOperationException("Seleccione la compra.");
        await _purchases.ReturnAsync(PurchaseLine.Id, Quantity, Reason, Key("SUPPLIER_RETURN"));
        Quantity = 0m;
        Summary = "Devolución registrada como crédito del proveedor; no implica recepción de efectivo.";
        await RefreshPurchaseAfterCommitAsync(purchase);
    }, "SUPPLIER_RETURN");
    private async Task RefreshPurchaseAfterCommitAsync(PurchaseDto purchase)
    {
        if (!ReferenceEquals(Purchase, purchase)) return;
        try
        {
            var fresh = await _purchases.GetByIdAsync(purchase.Id)
                ?? throw new InvalidOperationException("No se pudo consultar la compra registrada.");
            if (!ReferenceEquals(Purchase, purchase)) return;
            int index = Purchases.IndexOf(purchase);
            if (index >= 0) Purchases[index] = fresh;
            Purchase = fresh;
        }
        catch (Exception)
        {
            if (!ReferenceEquals(Purchase, purchase)) return;
            Purchase = null;
            Purchases.Remove(purchase);
            Summary += " Operación confirmada. No se pudo actualizar el detalle; vuelva a consultarlo sin repetir la operación.";
        }
    }
    [RelayCommand]
    private Task CashMovementAsync() => RunAsync(async () =>
    {
        await _shifts.RegisterMovementAsync(Amount, IsOutflow, Reason, Key("CASH"));
        Amount = 0m;
    }, "CASH");
    [RelayCommand]
    private Task CreateWalletAsync() => RunAsync(() => _wallets.CreateCardAsync(WalletNumber, 0m, Customer?.Id), "WALLET_CREATE");
    [RelayCommand]
    private Task LoadWalletAsync() => RunAsync(async () =>
    {
        var card = await _wallets.GetByCardNumberAsync(WalletNumber) ?? throw new InvalidOperationException("Monedero inexistente.");
        var shift = await _shifts.GetActiveAsync() ?? throw new InvalidOperationException("Abra un turno.");
        await _wallets.RechargeAsync(card.Id, Amount, PaymentMethod.Efectivo, shift.Id, Key("WALLET_LOAD"));
        Amount = 0m;
    }, "WALLET_LOAD");
    [RelayCommand]
    private Task WalletStatementAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.WalletManage);
        await StartLedgerAsync("Giftcard", WalletNumber.Trim(), WalletNumber.Trim());
    });
    [RelayCommand(CanExecute = nameof(CanSettleSelectedRefund))]
    private Task SettleRefundAsync() => RunAsync(async () =>
    {
        var selected = Refund;
        if (selected is null || !PendingRefunds.Contains(selected) || selected.RequiresReview || !ExternalRefundVerified)
            throw new InvalidOperationException("Seleccione un pendiente vigente sin inconsistencias y confirme su comprobación bancaria.");
        int version = _bankRefundNavigation.Version;
        try { await _reconciliation.SettleRefundAsync(selected.Id, Reference, Key("REFUND")); }
        catch when (version != _bankRefundNavigation.Version) { return; }
        _keys.Remove("REFUND");
        if (version != _bankRefundNavigation.Version) return;
        PendingRefunds.Remove(selected); Refund = null;
        await RefreshRefundsAfterCommitAsync(false, $"Reembolso {selected.Id} conciliado. No vuelva a mover dinero.");
    });
    [RelayCommand]
    private Task ReconcileLegacyShiftAsync() => RunAsync(async () =>
    {
        if (LegacyShift is null || !LegacyCountVerified) throw new InvalidOperationException("Seleccione el turno histórico y confirme el conteo físico y su acta.");
        await _shifts.ReconcileLegacyAsync(LegacyShift.Id, Amount, Reason, Key("LEGACY_SHIFT"));
        LegacyShifts.Remove(LegacyShift); LegacyShift = null; LegacyCountVerified = false;
        Summary = "Cierre histórico documentado. Abra un turno nuevo con su fondo contado. Este cierre no reconstruye movimientos faltantes.";
    }, "LEGACY_SHIFT");
    [RelayCommand]
    private Task QueryCheckoutBankAsync() => RunAsync(async () =>
    {
        CheckoutBankStatus = ""; CheckoutFundsVerified = false;
        if (_terminalReconciliation is null || PendingCheckout is null)
            throw new InvalidOperationException("Seleccione el cobro bancario pendiente.");
        var selected = PendingCheckout;
        string providerOrder = CheckoutProviderOrderId;
        var result = await _terminalReconciliation.RefreshPaymentAsync(selected.OperationId, providerOrder);
        if (PendingCheckout != selected || CheckoutProviderOrderId != providerOrder) return;
        if (!result.IsSuccess || result.Data is null)
        {
            CheckoutBankStatus = "Consulta no confirmada. Conserve la referencia y no vuelva a cobrar.";
            throw new InvalidOperationException(result.ErrorMessage);
        }
        CheckoutBankStatus = result.Data.CanComplete
            ? "Pago bancario confirmado para esta referencia. Verifique también el efectivo, si existe, antes de resolver."
            : result.Data.NoFundsConfirmed
                ? "La orden consultada terminó sin fondos confirmados. Compruebe los demás medios antes de descartar."
                : "El banco aún no permite resolver el cobro. No cree otra orden; conserve la referencia.";
    });
    [RelayCommand]
    private Task ResolveCheckoutAsync() => RunAsync(async () =>
    {
        if (_sales is null || PendingCheckout is null) throw new InvalidOperationException("Seleccione el intento de cobro.");
        var selected = PendingCheckout;
        int version = _pendingCheckoutVersion;
        await _sales.ResolveInterruptedCheckoutAsync(selected.OperationId, CompleteCheckout, CheckoutFundsVerified, Reason);
        if (version != _pendingCheckoutVersion) return;
        PendingCheckouts.Remove(selected); PendingCheckout = null; CheckoutFundsVerified = false;
        Summary = "Resolución registrada. No se solicitó un nuevo cargo a la terminal.";
        try { await LoadPendingCheckoutPageAsync(_pendingCheckoutPositions[_pendingCheckoutPage], _pendingCheckoutPage); }
        catch { Summary += " No se pudo actualizar la lista. Consulte de nuevo; no repita el cobro."; }
    }, "CHECKOUT");
    private int _reconciliationPage, _reconciliationVersion;
    [ObservableProperty] private bool _hasMoreReconciliation;
    public bool HasPreviousReconciliation => _reconciliationPage > 0;
    partial void OnDayChanged(string value) => ResetReconciliation();
    partial void OnCanReconcileChanged(bool value) { if (!value) ResetReconciliation(); }
    private void ResetReconciliation()
    {
        ++_reconciliationVersion; _reconciliationPage = 0; HasMoreReconciliation = false;
        Rows.Clear(); Summary = ""; OnPropertyChanged(nameof(HasPreviousReconciliation));
    }
    [RelayCommand] private Task ReconcileAsync() { ResetReconciliation(); return RunAsync(LoadReconciliationAsync); }
    [RelayCommand]
    private Task NextReconciliationAsync() => RunAsync(async () =>
    { if (HasMoreReconciliation) { ++_reconciliationPage; await LoadReconciliationAsync(); } });
    [RelayCommand]
    private Task PreviousReconciliationAsync() => RunAsync(async () =>
    { if (_reconciliationPage > 0) { --_reconciliationPage; await LoadReconciliationAsync(); } });
    private async Task LoadReconciliationAsync()
    {
        int version = ++_reconciliationVersion;
        Rows.Clear(); Summary = "Consulta de conciliación pendiente.";
        HasMoreReconciliation = false; OnPropertyChanged(nameof(HasPreviousReconciliation));
        if (!DateTime.TryParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)) throw new InvalidOperationException("Use la fecha aaaa-mm-dd.");
        ReconciliationPage page;
        try { page = await _reconciliation.ReconcilePageAsync(date, _reconciliationPage); }
        catch when (version != _reconciliationVersion) { return; }
        catch { if (version == _reconciliationVersion) Summary = "Conciliación no disponible. No equivale a saldos cero ni a ausencia de pendientes."; throw; }
        if (version != _reconciliationVersion) return;
        var result = page.Items; HasMoreReconciliation = page.HasMore;
        foreach (var r in result) Rows.Add(new(Day, r.ShiftId, r.IsBalanced ? "CONCILIADO INTERNO" : "REVISAR: " + string.Join("; ", r.Issues.Select(i => i.Message)),
            r.ExpectedCash, r.Difference, r.CashierUserId ?? "Sin atribución", r.RegisterId ?? "Sin caja", r.ShiftId));
        Summary = $"Página {_reconciliationPage + 1}: {result.Count} de {page.TotalCount} turnos. Requieren revisión en esta página: {result.Count(r => !r.IsBalanced)}. "
            + (result.Any(r => r.Payments is null) ? "Hay turnos sin cálculo: no se muestran totales parciales como completos. " :
                $"Pagos de esta página: {result.Sum(r => r.Payments):C2}; reembolsos pendientes de esta página: {result.Sum(r => r.PendingRefunds):C2}. ")
            + "La conciliación interna no sustituye comprobar banco, inventario y estados de cuenta.";
    }
}
