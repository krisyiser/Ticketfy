using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Inventory;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.ViewModels.Settings;
using Ticketfy.ViewModels.Shell;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task NavigationAndInventoryQueryCompletingAfterLogout_CannotRestoreAdministrativeScreen()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var operations = new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER");
        var pending = new TaskCompletionSource<InventoryCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var products = new DeferredInventoryProducts(ProductsService(), () => pending.Task);
        var inventory = new InventoryViewModel(products, new NoNetworkCatalog());
        var login = new LoginViewModel(new AuthService(new UserService(db)), session, null!);
        var navigation = new NavigationService(login, null!, inventory, null!, null!, null!, null!, null!, null!, null!, null!,
            operations, new TestDbContextFactory(_options), session);
        navigation.GoToPos();
        var navigating = navigation.NavigateTo("inventory");
        Assert.False(navigating.IsCompleted);
        Assert.Equal(2, products.Requests);
        navigation.GoToLogin();
        pending.SetResult(new([(await ProductsService().GetByIdAsync("P"))!], false, 60m, 200m, true));
        await navigating;
        Assert.True(navigation.IsAtLogin);
        Assert.Empty(inventory.FilteredProducts);
        Assert.Equal(0m, inventory.TotalStockCost);
        Assert.Equal("Restringido", inventory.TotalStockCostDisplay);
        Assert.Empty(navigation.AuthorizationError);
    }

    [AvaloniaFact]
    public async Task InventoryReadFailure_RemovesPreviouslyVisibleCostsAndProducts()
    {
        var products = new DeferredInventoryProducts(ProductsService(), async () => await ProductsService().GetInventoryPageAsync(0));
        var catalog = new InventoryCatalogViewModel(products);
        await catalog.LoadProductsAsync();
        Assert.Single(catalog.Products);
        Assert.Equal(60m, catalog.TotalStockCost);
        products.Read = () => Task.FromException<InventoryCatalogPage>(new UnauthorizedAccessException("synthetic-sensitive-error"));
        await catalog.LoadProductsAsync();
        Assert.Empty(catalog.Products);
        Assert.Empty(catalog.FilteredProducts);
        Assert.Equal(0m, catalog.TotalStockCost);
        Assert.Equal(0m, catalog.TotalStockSale);
        Assert.Equal("Restringido", catalog.TotalStockCostDisplay);
        Assert.DoesNotContain("synthetic-sensitive-error", catalog.LoadMessage);
    }

    [AvaloniaFact]
    public async Task SessionSwitch_ClearsHistoryExpensesAndSettingsSecretsWithoutReusingSections()
    {
        await using var db = Db();
        var session = new SessionManager();
        var user = new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync());
        session.StartSession(user);
        var saved = await Sales.SaveAsync(Sale("UI-SESSION-SALE"));
        var history = new HistoryViewModel(Sales, null!);
        await history.LoadSalesAsync();
        history.ViewSaleDetailCommand.Execute(saved);
        var expenses = new ExpensesViewModel(new ExpenseService(new TestDbContextFactory(_options), _actor));
        await expenses.LoadExpensesAsync();
        var settings = new SettingsViewModel();
        var oldSection = settings.Sections.Single(s => s.ViewModel is UsuariosSettingsViewModel);
        var oldUsers = (UsuariosSettingsViewModel)oldSection.ViewModel;
        oldUsers.IsAdminUnlocked = true; oldUsers.AdminPasswordInput = "synthetic-secret"; oldUsers.NewPassword = "synthetic-new-secret";
        var oldRecovery = (SeguridadSettingsViewModel)settings.Sections.Single(s => s.ViewModel is SeguridadSettingsViewModel).ViewModel;
        oldRecovery.RecoveryPassphrase = oldRecovery.RecoveryPassphraseConfirmation = "synthetic-recovery-secret";
        oldRecovery.RecoveryCustodyConfirmed = true;
        var login = new LoginViewModel(new AuthService(new UserService(db)), session, null!);
        var navigation = new NavigationService(login, null!, null!, null!, history, null!, null!, settings, null!, expenses, null!, session: session);
        navigation.GoTo(history);
        session.SwitchCashier(user); // Re-authenticating the same ID must also discard its old UI session.
        Assert.True(navigation.IsAtPos);
        Assert.Empty(history.Sales);
        Assert.Empty(history.HourlyReport);
        Assert.False(history.IsSaleDetailDialogOpen);
        Assert.Null(history.SelectedSaleForDetail);
        Assert.Equal(0m, history.TotalDayRevenue);
        Assert.Equal(0m, expenses.TotalEnCaja);
        Assert.Empty(expenses.Expenses);
        Assert.False(oldUsers.IsAdminUnlocked);
        Assert.Empty(oldUsers.AdminPasswordInput);
        Assert.Empty(oldUsers.NewPassword);
        Assert.Empty(oldRecovery.RecoveryPassphrase);
        Assert.Empty(oldRecovery.RecoveryPassphraseConfirmation);
        Assert.False(oldRecovery.RecoveryCustodyConfirmed);
        Assert.DoesNotContain(oldSection, settings.Sections);
        settings.SelectSectionCommand.Execute(oldSection);
        Assert.NotSame(oldUsers, settings.CurrentSectionViewModel);
    }

    [AvaloniaFact]
    public async Task HistoryHourlySummary_IncludesTicketsBeyondVisibleFiveHundred()
    {
        await using var db = Db();
        for (var i = 0; i < 501; i++) db.Sales.Add(new SaleEntity
        {
            Id = $"HOURLY-{i:D3}",
            OperationId = $"HOURLY-{i:D3}",
            Date = Ticketfy.Core.Helpers.BusinessTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Total = 1m,
            PaidAmount = 1m,
            CashAmount = 1m
        });
        await db.SaveChangesAsync();
        var history = new HistoryViewModel(Sales, null!);
        await history.LoadSalesAsync();
        Assert.Equal(100, history.Sales.Count);
        Assert.Equal(501m, history.TotalDayRevenue);
        Assert.Equal(501, history.HourlyReport.Sum(h => h.TicketCount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.GetSalesByDateRangeAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today));
        Assert.Contains("501", history.FeedbackMessage);
        for (var page = 1; page <= 5; page++) await history.NextSalesPageCommand.ExecuteAsync(null);
        Assert.Single(history.Sales);
        Assert.Equal(501m, history.TotalDayRevenue);
        Assert.False(history.HasMoreSales);
        await history.PreviousSalesPageCommand.ExecuteAsync(null);
        Assert.Equal(100, history.Sales.Count);
        history.ClearSessionData();
    }

    [AvaloniaFact]
    public async Task ShiftPromptCompletingAfterLogout_CannotOpenOrDismissAnotherSessionsDialog()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var pending = new TaskCompletionSource<ShiftDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShiftCoordinatorViewModel(new DeferredShift(pending.Task), session, null!, null!, null!);
        var dialogCount = 0;
        coordinator.ShowDialogRequested += _ => dialogCount++;
        var validating = coordinator.ValidateShiftStatusAsync();
        session.ClearSession();
        pending.SetResult(null);
        Assert.False(await validating);
        Assert.Equal(0, dialogCount);
    }

    [AvaloniaFact]
    public async Task OpenShiftDialogWait_IsCompletedWhenSessionLocks()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var coordinator = new ShiftCoordinatorViewModel(new DeferredShift(Task.FromResult<ShiftDto?>(null)), session, null!, null!, null!);
        var opened = 0; var closed = 0;
        coordinator.ShowDialogRequested += _ => opened++;
        coordinator.CloseDialogRequested += () => closed++;
        var validating = coordinator.ValidateShiftStatusAsync();
        Assert.Equal(1, opened);
        Assert.False(validating.IsCompleted);
        session.LockTerminal();
        Assert.False(await validating.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, closed);
    }

    private sealed class DeferredShift(Task<ShiftDto?> pending) : IShiftService
    {
        public Func<Task<CashupHistoryPage>>? CashupRead { get; set; }
        public Task<CashupHistoryPage> GetCashupPageAsync(DateTime start, DateTime end, int page, int size = 100)
            => CashupRead?.Invoke() ?? throw new NotSupportedException();
        public Task<ShiftDto?> GetActiveAsync() => pending;
        public Task<ShiftDto> OpenAsync(decimal openingBalance) => throw new NotSupportedException();
        public Task<ShiftDto> CloseAsync(string shiftId, decimal actualBalance) => throw new NotSupportedException();
        public Task<List<ShiftDto>> GetAllAsync() => throw new NotSupportedException();
    }

    [Fact]
    public async Task ShiftCashReads_RequireLiveSessionAndOwnershipWithoutLoadingProfitData()
    {
        var factory = new TestDbContextFactory(_options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftService(factory).GetActiveAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftService(factory).GetActiveCashSummaryAsync());
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var service = new ShiftService(factory, new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER"));
        await Sales.SaveAsync(Sale("CASH-SUMMARY"));
        await service.RegisterMovementAsync(3m, true, "Synthetic withdrawal", "CASH-SUMMARY-WITHDRAW");
        var summary = await service.GetActiveCashSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal(20m, summary.Inflows);
        Assert.Equal(3m, summary.Outflows);
        Assert.Equal(17m, summary.Shift.ExpectedBalance);
        session.LockTerminal();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveCashSummaryAsync());
        var other = new UserEntity { Id = Guid.NewGuid(), Username = "other", FullName = "Synthetic other", RoleString = "CAJERO", IsActive = true };
        db.Users.Add(other); await db.SaveChangesAsync();
        session.StartSession(new UserRolePermissionEngine().MapToModel(other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveCashSummaryAsync());
    }

    [AvaloniaFact]
    public async Task CashupFinishingPrintAfterUserChange_DoesNotLogOutNewOperatorOrPrintWrongAttribution()
    {
        await using var db = Db();
        var session = new SessionManager();
        var admin = new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync());
        session.StartSession(admin);
        var service = new ShiftService(new TestDbContextFactory(_options),
            new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER"));
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var printer = new DeferredCountPrinter(pending.Task);
        var model = new Ticketfy.ViewModels.Dialogs.CashupDialogViewModel(service, session, printer, isFinalZCut: true);
        var closed = 0;
        model.RequestClose += () => closed++;
        var saving = model.SaveCashupCommand.ExecuteAsync(null);
        Assert.False(saving.IsCompleted);
        Assert.NotNull(printer.Slip);
        var replacement = new UserModel { Username = "replacement", FullName = "Synthetic replacement" };
        session.StartSession(replacement);
        pending.SetResult(true);
        await saving;
        Assert.Same(replacement, session.CurrentCashier);
        Assert.Equal(0, closed);
        Assert.Equal(admin.FullName, printer.Slip.CashierName);
        Assert.Equal(0, (await db.Shifts.AsNoTracking().SingleAsync()).IsOpen);
        var savedCut = Assert.Single(await db.Cashups.ToListAsync());
        Assert.True(Ticketfy.Core.Helpers.BusinessTime.TryLocal(savedCut.Timestamp, out var recordedAt));
        Assert.Equal(recordedAt, printer.Slip.Timestamp);
    }

    private sealed class DeferredCountPrinter(Task<bool> pending) : IEscPosPrinterService
    {
        public Ticketfy.Core.Models.ShiftMovementSlipModel? Slip { get; private set; }
        public Task<bool> PrintNonSaleCashMovementSlipAsync(Ticketfy.Core.Models.ShiftMovementSlipModel model, string printerPortOrName = "")
        { Slip = model; return pending; }
        public Task<bool> PrintTicketAsync(SaleDto sale, string printerPortOrName = "") => throw new NotSupportedException();
        public Task<bool> OpenCashDrawerAsync(string printerPortOrName = "", int drawerPin = 0) => throw new NotSupportedException();
        public Task<bool> PrintTestPageAsync(string printerPortOrName = "") => throw new NotSupportedException();
        public Task<bool> IsPrinterOnlineAsync() => throw new NotSupportedException();
        public Task<bool> PrintInventoryChecklistAsync(List<ProductDto> products, string printerPortOrName = "") => throw new NotSupportedException();
        public Task<bool> PrintSnapshotChecklistAsync(InventorySnapshotEntity snapshot, string printerPortOrName = "") => throw new NotSupportedException();
        public Task<bool> PrintPurchaseOrderAsync(PurchaseDto purchase, string printerPortOrName = "") => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public async Task ExpenseReadFailure_DoesNotDiscardIdentityOfCommittedCommandWithLostResponse()
    {
        await Sales.SaveAsync(Sale("EXPENSE-RETRY-FLOAT"));
        var factory = new TestDbContextFactory(_options);
        var expenses = new LostExpenseResponse(new ExpenseService(factory, _actor));
        var model = new ExpensesViewModel(expenses)
        { ExpenseAmount = 5m, ExpenseDescription = "Synthetic expense" };
        await model.AddExpenseCommand.ExecuteAsync(null);
        Assert.Equal(5m, model.ExpenseAmount);
        expenses.FailReads = true;
        await model.LoadExpensesAsync();
        Assert.Equal(5m, model.ExpenseAmount);
        Assert.Equal("Synthetic expense", model.ExpenseDescription);
        expenses.FailReads = false;
        await model.AddExpenseCommand.ExecuteAsync(null);
        Assert.Equal(2, expenses.Operations.Count);
        Assert.Single(expenses.Operations.Distinct());
        await using var db = Db();
        Assert.Single(await db.Expenses.ToListAsync());
        Assert.Equal(15m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    private sealed class LostExpenseResponse(IExpenseService inner) : IExpenseService
    {
        public bool FailReads { get; set; }
        public List<string> Operations { get; } = [];
        public async Task<ExpenseDto> CreateAsync(ExpenseDto dto)
        {
            Operations.Add(dto.OperationId!);
            var result = await inner.CreateAsync(dto);
            if (Operations.Count == 1) throw new IOException("synthetic lost response after commit");
            return result;
        }
        public Task<List<ExpenseDto>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
            => FailReads ? Task.FromException<List<ExpenseDto>>(new UnauthorizedAccessException()) : inner.GetAllAsync(startDate, endDate, shiftId);
        public Task<ExpensePageDto> GetPageAsync(int page, int size = 100, DateTime? startDate = null, DateTime? endDate = null,
            string? shiftId = null, bool activeShiftOnly = false, bool cancellationsOnly = false)
            => FailReads ? Task.FromException<ExpensePageDto>(new UnauthorizedAccessException())
                : inner.GetPageAsync(page, size, startDate, endDate, shiftId, activeShiftOnly, cancellationsOnly);
        public Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null)
            => inner.GetFinancialSummaryAsync(startDate, endDate, shiftId);
        public Task<NetProfitReportModel> CalculateTrueNetProfitAsync(DateTime startDate, DateTime endDate) => inner.CalculateTrueNetProfitAsync(startDate, endDate);
        public Task<bool> DeleteAsync(string id) => inner.DeleteAsync(id);
    }

    [AvaloniaFact]
    public async Task CheckoutCompletingAfterDialogReplacement_PreservesNewTicketAndOverlay()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var customers = CustomersService();
        using var pos = new PosViewModel(ProductsService(), customerService: customers, sessionManager: session, externalCatalogService: new NoNetworkCatalog());
        var inventory = new InventoryViewModel(ProductsService(), new NoNetworkCatalog());
        var history = new HistoryViewModel(Sales, null!);
        var dialogs = new DialogCoordinator();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var print = new DeferredSalePrinter(pending.Task);
        var wirer = new DialogWirer(dialogs, ProductsService(), customers, Sales, null!, print, null!, null!, session,
            null!, null!, null!, null!, null!, null!, null!, null!, null!);
        wirer.WirePosVm(pos, inventory, history);
        pos.Cart.CartState.AddItem(UiCart()[0], 20);
        await pos.Cart.CheckoutCommand.ExecuteAsync("Efectivo");
        var checkout = Assert.IsType<Ticketfy.ViewModels.Dialogs.CheckoutDialogViewModel>(dialogs.ActiveDialogViewModel);
        checkout.ReceivedAmountInput = "20";
        var saving = checkout.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(1, print.Calls);
        Assert.False(saving.IsCompleted);
        dialogs.CloseDialog();
        session.ClearSession();
        pos.Cart.CartState.AddItem(new CartItemDto("P", "New ticket", 10m, 1), 18);
        var replacement = new Ticketfy.ViewModels.Dialogs.CustomerDialogViewModel(customers);
        dialogs.ShowDialog(replacement);
        Assert.False(dialogs.TryCloseDialog(checkout));
        pending.SetResult();
        await saving;
        Assert.Same(replacement, dialogs.ActiveDialogViewModel);
        Assert.True(dialogs.IsDialogOverlayOpen);
        Assert.Equal(10m, pos.Cart.CartState.Total);
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(20m, (await db.Shifts.AsNoTracking().SingleAsync()).ExpectedBalance);
        Assert.True(dialogs.TryCloseDialog(replacement));
        Assert.False(dialogs.IsDialogOverlayOpen);
    }

    private sealed class DeferredSalePrinter(Task pending) : IPrintDispatcherService
    {
        public int Calls { get; private set; }
        public Task DispatchSaleDocumentsAsync(SaleDto sale, bool printReceipt = true) { Calls++; return pending; }
    }

    private sealed class DeferredInventoryProducts(IProductService inner, Func<Task<InventoryCatalogPage>> read) : IProductService
    {
        public Func<Task<ProductEditorOptions>>? EditorRead { get; set; }
        public Task<ProductEditorOptions> GetEditorOptionsAsync(string? productId = null, string? supplierSearch = null)
            => EditorRead?.Invoke() ?? inner.GetEditorOptionsAsync(productId, supplierSearch);
        public Func<Task<InventoryCatalogPage>> Read { get; set; } = read;
        public int Requests { get; private set; }
        public Task<InventoryCatalogPage> GetInventoryPageAsync(int page, int size = 100, string? search = null, bool lowStockOnly = false)
        { Requests++; return Read(); }
        public Task<double> GetDefaultMinStockAsync() => Task.FromResult(5d);
        public Task<List<ProductDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<ProductCatalogPage> GetPageAsync(int page, int size = 60, string? search = null, string? category = null)
            => inner.GetPageAsync(page, size, search, category);
        public Task<ProductCatalogPage> GetPurchasePageAsync(int page, int size = 100, string? search = null, string? supplierId = null)
            => inner.GetPurchasePageAsync(page, size, search, supplierId);
        public Task<List<ProductCategoryCount>> GetCategoryCountsAsync() => inner.GetCategoryCountsAsync();
        public Task<List<ProductDto>> GetByCategoryAsync(string category) => inner.GetByCategoryAsync(category);
        public Task<ProductDto?> GetByBarcodeAsync(string barcode) => inner.GetByBarcodeAsync(barcode);
        public Task<ProductDto?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task AddAsync(ProductDto product) => inner.AddAsync(product);
        public Task UpdateAsync(ProductDto product) => inner.UpdateAsync(product);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task ClearInventoryAsync() => inner.ClearInventoryAsync();
        public Task BulkSaveAsync(IEnumerable<ProductDto> products) => inner.BulkSaveAsync(products);
        public Task<int> ImportFromCsvTextAsync(string text) => inner.ImportFromCsvTextAsync(text);
        public Task<List<ProductDto>> SearchFtsAsync(string query) => inner.SearchFtsAsync(query);
        public Task<IEnumerable<ProductDto>> GetCatalogForPosAsync() => inner.GetCatalogForPosAsync();
        public Task<bool> AdjustStockManuallyAsync(string id, double stock, string reason, string userId, ISecurityInterceptionService? securityService = null, IAuditService? auditService = null)
            => inner.AdjustStockManuallyAsync(id, stock, reason, userId, securityService, auditService);
    }
}
