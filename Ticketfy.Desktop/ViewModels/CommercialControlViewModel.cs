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

public sealed record CommercialLedgerRow(string Date, string Document, string Event, decimal Amount, decimal Balance,
    string User, string Register, string Shift);
public sealed record PendingRefundChoice(string Id, string Label);

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
    private readonly Dictionary<string, string> _keys = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private ProductDto? _product;
    [ObservableProperty] private CustomerDto? _customer;
    [ObservableProperty] private SupplierDto? _supplier;
    [ObservableProperty] private PurchaseDto? _purchase;
    [ObservableProperty] private PurchaseItemDto? _purchaseLine;
    [ObservableProperty] private PendingRefundChoice? _refund;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _method = "Efectivo";
    [ObservableProperty] private string _walletNumber = "";
    [ObservableProperty] private string _day = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private bool _isOutflow = true;
    [ObservableProperty] private bool _externalRefundVerified;
    [ObservableProperty] private bool _canInventory;
    [ObservableProperty] private bool _canCustomers;
    [ObservableProperty] private bool _canSuppliers;
    [ObservableProperty] private bool _canCash;
    [ObservableProperty] private bool _canWallet;
    [ObservableProperty] private bool _canReconcile;
    [ObservableProperty] private ShiftDto? _legacyShift;
    [ObservableProperty] private bool _legacyCountVerified;
    public ObservableCollection<ShiftDto> LegacyShifts { get; } = [];
    public ObservableCollection<PendingCheckoutDto> PendingCheckouts { get; } = [];
    [ObservableProperty] private PendingCheckoutDto? _pendingCheckout;
    [ObservableProperty] private bool _checkoutFundsVerified;
    [ObservableProperty] private bool _completeCheckout = true;
    public ObservableCollection<ProductDto> Products { get; } = [];
    public ObservableCollection<CustomerDto> Customers { get; } = [];
    public ObservableCollection<SupplierDto> Suppliers { get; } = [];
    public ObservableCollection<PurchaseDto> Purchases { get; } = [];
    public ObservableCollection<PurchaseItemDto> PurchaseLines { get; } = [];
    public ObservableCollection<PendingRefundChoice> PendingRefunds { get; } = [];
    public ObservableCollection<CommercialLedgerRow> Rows { get; } = [];
    public string[] PaymentMethods { get; } = ["Efectivo", "Transferencia", "Tarjeta"];

    public CommercialControlViewModel(IProductService products, ICustomerService customers, ISupplierService suppliers,
        IPurchaseService purchases, IShiftService shifts, IGiftcardService wallets, CommercialReconciliationService reconciliation,
        IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations, ISaleService? sales = null)
    {
        _products = products; _customers = customers; _suppliers = suppliers; _purchases = purchases; _shifts = shifts;
        _wallets = wallets; _reconciliation = reconciliation; _factory = factory; _operations = operations;
        _sales = sales;
    }
    partial void OnPurchaseChanged(PurchaseDto? value)
    {
        PurchaseLines.Clear();
        foreach (var line in value?.Items ?? []) PurchaseLines.Add(line);
        PurchaseLine = PurchaseLines.FirstOrDefault();
    }
    private string Key(string command)
    {
        if (!_keys.TryGetValue(command, out var key)) _keys[command] = key = Guid.NewGuid().ToString("N");
        return key;
    }
    private async Task RunAsync(Func<Task> action, string? command = null)
    {
        if (IsBusy) return;
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
        CanReconcile = await CanAsync(PermissionCatalog.ReportsView);
        CanHistorical = await CanAsync(PermissionCatalog.HistoryReconcile);
        HistoricalCustomers.Clear(); HistoricalPurchases.Clear(); InvalidateHistoricalPreview();
        HistoricalCustomer = null; HistoricalPurchase = null;
        if (CanHistorical)
        {
            foreach (var target in await Historical.GetTargetsAsync(HistoricalBalanceScope.CustomerAccount)) HistoricalCustomers.Add(target);
            foreach (var target in await Historical.GetTargetsAsync(HistoricalBalanceScope.SupplierPurchase)) HistoricalPurchases.Add(target);
        }
        CanTransfers = await CanAsync(PermissionCatalog.PaymentsReconcile);
        if (CanTransfers) await LoadPendingTransfersAsync();
        Products.Clear(); if (CanInventory) foreach (var p in await _products.GetAllAsync()) Products.Add(p);
        Customers.Clear(); if (CanCustomers) foreach (var c in await _customers.GetAllAsync()) Customers.Add(c);
        Suppliers.Clear(); if (CanSuppliers) foreach (var s in await _suppliers.GetAllAsync()) Suppliers.Add(s);
        Purchases.Clear(); if (CanSuppliers) foreach (var p in await _purchases.GetAllAsync()) Purchases.Add(p);
        PendingRefunds.Clear();
        LegacyShifts.Clear();
        PendingCheckouts.Clear();
        try
        {
            foreach (var s in await _shifts.GetLegacyOpenAsync()) LegacyShifts.Add(s);
            if (_sales is not null) foreach (var c in await _sales.GetInterruptedCheckoutsAsync()) PendingCheckouts.Add(c);
            foreach (var r in await _reconciliation.GetPendingRefundsAsync())
                PendingRefunds.Add(new(r.Id, $"{r.ReturnId} · {r.Kind} · {r.Amount:C2}"));
        }
        catch (UnauthorizedAccessException) { Message = "Su rol no puede conciliar reembolsos bancarios."; }
    });
    [RelayCommand]
    private Task KardexAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.InventoryView);
        if (Product is null) throw new InvalidOperationException("Seleccione un producto.");
        Rows.Clear();
        var moves = await _products.GetKardexAsync(Product.Id);
        foreach (var m in moves) Rows.Add(new(m.CreatedAtUtc, m.DocumentId, m.Reason, m.Quantity, m.StockAfter, m.CashierUserId, m.RegisterId, m.ShiftId ?? "Migración"));
        Summary = $"Kardex de {Product.Name}. Importes de la tabla expresados en {Product.Unit}. Existencia: {moves.LastOrDefault()?.StockAfter ?? 0:N3}";
    });
    private void ShowStatement(AccountStatementDto statement, string name)
    {
        Rows.Clear(); decimal balance = 0m;
        foreach (var e in statement.Entries)
        {
            balance += e.Amount;
            Rows.Add(new(e.CreatedAtUtc, e.DocumentId, e.EntryType, e.Amount, balance, e.CashierUserId, e.RegisterId, e.ShiftId ?? "Migración"));
        }
        Summary = $"{name} · Saldo: {statement.Balance:C2}. Negativo = saldo a favor.";
        if (statement.HasUnreconciledHistory) Summary += " ATENCIÓN: hay saldos históricos sin verificar o una diferencia entre saldo y ledger. Revise Históricos antes de dar el saldo por conciliado.";
    }
    [RelayCommand]
    private Task CustomerStatementAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.CustomersView);
        if (Customer is null) throw new InvalidOperationException("Seleccione un cliente.");
        ShowStatement(await _customers.GetStatementAsync(Customer.Id), Customer.Name);
    });
    [RelayCommand]
    private Task SupplierStatementAsync() => RunAsync(async () =>
    {
        await RequireAsync(PermissionCatalog.SuppliersManage);
        if (Supplier is null) throw new InvalidOperationException("Seleccione un proveedor.");
        ShowStatement(await _purchases.GetStatementAsync(Supplier.Id), Supplier.Name);
    });
    [RelayCommand]
    private Task PaySupplierAsync() => RunAsync(async () =>
    {
        if (Purchase is null) throw new InvalidOperationException("Seleccione la compra a pagar.");
        await _purchases.PayAsync(Purchase.Id, Amount, Method, Reference, Key("SUPPLIER_PAY"));
        Amount = 0m;
        ShowStatement(await _purchases.GetStatementAsync(Purchase.SupplierId), Purchase.SupplierName);
    }, "SUPPLIER_PAY");
    [RelayCommand]
    private Task ReturnSupplierAsync() => RunAsync(async () =>
    {
        if (PurchaseLine is null) throw new InvalidOperationException("Seleccione la partida de la compra.");
        await _purchases.ReturnAsync(PurchaseLine.Id, Quantity, Reason, Key("SUPPLIER_RETURN"));
        Quantity = 0m;
        Summary = "Devolución registrada como crédito del proveedor; no implica recepción de efectivo.";
    }, "SUPPLIER_RETURN");
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
        await using var db = await _factory.CreateDbContextAsync();
        var entries = await db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == AccountKind.Giftcard && e.AccountId == WalletNumber)
            .OrderBy(e => e.CreatedAtUtc).ToListAsync();
        ShowStatement(new(WalletNumber, entries.Sum(e => e.Amount), entries), WalletNumber);
    });
    [RelayCommand]
    private Task SettleRefundAsync() => RunAsync(async () =>
    {
        if (Refund is null || !ExternalRefundVerified) throw new InvalidOperationException("Seleccione el reembolso y confirme su comprobación bancaria.");
        await _reconciliation.SettleRefundAsync(Refund.Id, Reference, Key("REFUND"));
        PendingRefunds.Remove(Refund); Refund = null; ExternalRefundVerified = false;
    }, "REFUND");
    [RelayCommand]
    private Task ReconcileLegacyShiftAsync() => RunAsync(async () =>
    {
        if (LegacyShift is null || !LegacyCountVerified) throw new InvalidOperationException("Seleccione el turno histórico y confirme el conteo físico y su acta.");
        await _shifts.ReconcileLegacyAsync(LegacyShift.Id, Amount, Reason, Key("LEGACY_SHIFT"));
        LegacyShifts.Remove(LegacyShift); LegacyShift = null; LegacyCountVerified = false;
        Summary = "Cierre histórico documentado. Abra un turno nuevo con su fondo contado. Este cierre no reconstruye movimientos faltantes.";
    }, "LEGACY_SHIFT");
    [RelayCommand]
    private Task ResolveCheckoutAsync() => RunAsync(async () =>
    {
        if (_sales is null || PendingCheckout is null) throw new InvalidOperationException("Seleccione el intento de cobro.");
        await _sales.ResolveInterruptedCheckoutAsync(PendingCheckout.OperationId, CompleteCheckout, CheckoutFundsVerified, Reason);
        PendingCheckouts.Remove(PendingCheckout); PendingCheckout = null; CheckoutFundsVerified = false;
        Summary = "Resolución registrada. No se solicitó un nuevo cargo a la terminal.";
    }, "CHECKOUT");
    [RelayCommand]
    private Task ReconcileAsync() => RunAsync(async () =>
    {
        if (!DateTime.TryParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)) throw new InvalidOperationException("Use la fecha aaaa-mm-dd.");
        var result = await _reconciliation.ReconcileAsync(date);
        Rows.Clear();
        foreach (var r in result) Rows.Add(new(Day, r.ShiftId, r.IsBalanced ? "CONCILIADO INTERNO" : "REVISAR: " + string.Join("; ", r.Issues.Select(i => i.Message)),
            r.ExpectedCash, r.Difference, r.CashierUserId ?? "Sin atribución", r.RegisterId ?? "Sin caja", r.ShiftId));
        Summary = $"Turnos revisados: {result.Count}. Requieren revisión: {result.Count(r => !r.IsBalanced)}. "
            + $"Pagos: {result.Sum(r => r.Payments):C2}; reembolsos pendientes: {result.Sum(r => r.PendingRefunds):C2}. "
            + "La conciliación interna no sustituye comprobar banco, inventario y estados de cuenta.";
    });
}
