using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Suppliers;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;
using Ticketfy.Views.Suppliers;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private static Window Show(Control view, object model)
    {
        view.DataContext = model;
        var window = new Window { Content = view, Width = 1200, Height = 900 };
        window.Show();
        return window;
    }
    private static async Task ClickTwiceAsync(Control view, IAsyncRelayCommand command)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, command));
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(button.CommandParameter);
        var pending = command.ExecutionTask!;
        button.Command.Execute(button.CommandParameter);
        await pending;
        await command.ExecuteAsync(null); // Also retry after the first response arrived.
    }
    private CustomerService CustomersService() => new(new TestDbContextFactory(_options), _actor);
    private ProductService ProductsService() => new(new TestDbContextFactory(_options), _actor);
    private sealed class TestPrintDispatcher(bool fail = false) : IPrintDispatcherService
    {
        public int Calls { get; private set; }
        public Task DispatchSaleDocumentsAsync(SaleDto sale, bool printReceipt = true)
        {
            Calls++;
            return fail ? Task.FromException(new IOException("Test printer disconnected")) : Task.CompletedTask;
        }
    }
    private sealed class EvidenceTerminal(Stage6CommercialLedgerTests test) : IPaymentTerminalService
    {
        public int Calls { get; private set; }
        public Task<Result<PaymentResponse>> ProcessPaymentAsync(decimal amount, string referenceId, CancellationToken cancellationToken)
        {
            Calls++;
            using var db = test.Db();
            CommercialTestSession.ConfirmCard(db, referenceId, amount);
            return Task.FromResult(Result<PaymentResponse>.Success(new() { TransactionId = "TEST-BANK:" + referenceId }));
        }
        public Task<Result> CancelPaymentIntentAsync(string referenceId) => Task.FromResult(Result.Success());
        public Task<bool> RequiresReconciliationAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
    private static List<CartItemDto> UiCart() => [new("P", "Producto", 10m, 2) { OriginalUnitPrice = 10m, Cost = 3m }];

    [AvaloniaFact]
    public async Task Ui_ProductCustomerPromotion_SaveThroughBoundButtonsOnlyOnce()
    {
        await using var db = Db();
        var products = ProductsService();
        var product = new ProductDialogViewModel(products) { Name = "Producto pantalla", Barcode = "UI-001", CostPrice = 2m, RetailPrice = 5m, Stock = 4, Unit = "Pza" };
        var productView = new ProductDialog();
        var window = Show(productView, product);
        await ClickTwiceAsync(productView, product.SaveCommand);
        Assert.Equal("", product.ErrorMessage);
        Assert.Equal(4, (await products.GetByBarcodeAsync("UI-001"))!.Stock);
        window.Close();

        var customer = new CustomerDialogViewModel(CustomersService()) { Name = "Cliente pantalla", Phone = "5512345678", CustomerCode = "UI-CUSTOMER", CreditLimit = 100m };
        var customerView = new CustomerDialog(); window = Show(customerView, customer);
        await ClickTwiceAsync(customerView, customer.SaveCommand);
        Assert.Equal("", customer.ErrorMessage);
        Assert.Single(await db.Customers.Where(c => c.CustomerCode == "UI-CUSTOMER").ToListAsync());
        window.Close();

        var promotions = new PromotionService(new TestDbContextFactory(_options), _actor);
        var promotion = new PromotionDialogViewModel(promotions) { Name = "Oferta UI", AllProducts = true, DiscountValueText = "10" };
        var promotionView = new PromotionDialog(); window = Show(promotionView, promotion);
        await ClickTwiceAsync(promotionView, promotion.SaveCommand);
        Assert.Equal("", promotion.ErrorMessage);
        Assert.Single(await db.Promotions.ToListAsync());
        Assert.Equal(2m, (await promotions.EvaluateAndApplyPromotionsAsync(UiCart())).Single().AppliedDiscountAmount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ui_MixedCheckoutAndReturn_KeepCardAndCashSeparate()
    {
        var terminal = new EvidenceTerminal(this);
        var checkout = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Mixto", CashAmount = 15m, CardAmount = 10m };
        var view = new CheckoutDialog(); var window = Show(view, checkout);
        await ClickTwiceAsync(view, checkout.ConfirmPaymentCommand);
        Assert.Equal("", checkout.ErrorMessage);
        window.Close();
        await using var db = Db();
        var sale = await Sales.GetByIdAsync((await db.Sales.SingleAsync()).Id);
        Assert.Equal(1, terminal.Calls);
        Assert.Equal(5m, sale!.ChangeAmount);
        Assert.Equal(10m, sale.CashAmount);
        Assert.Equal(2, await db.SalePayments.CountAsync());

        var returns = new ReturnDialogViewModel(Sales, sale) { ReturnQuantity = 1 };
        var returnView = new ReturnDialog(); window = Show(returnView, returns);
        await ClickTwiceAsync(returnView, returns.ProcessReturnCommand);
        Assert.Equal("", returns.FeedbackMessage);
        Assert.Single(await db.Returns.ToListAsync());
        Assert.Equal(5m, (await db.Shifts.AsNoTracking().SingleAsync()).ExpectedBalance);
        Assert.Equal(5m, (await db.PaymentRefunds.SingleAsync(r => r.Kind == TenderKind.Card)).Amount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ui_PartialLoyaltyAndCash_PrintFailureCannotChargeAgain()
    {
        Ticketfy.Core.Messages.HardwareFeedbackMessage? outcome = null;
        var receiver = new object();
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<Ticketfy.Core.Messages.HardwareFeedbackMessage>(receiver, (_, message) => outcome = message);
        await using var db = Db();
        var customer = await db.Customers.SingleAsync(); customer.PuntosSaldo = 5; await db.SaveChangesAsync();
        var printer = new TestPrintDispatcher(fail: true);
        var checkout = new CheckoutDialogViewModel(Sales, CustomersService(), printer, new EvidenceTerminal(this), UiCart(), 20m);
        var view = new CheckoutDialog(); var window = Show(view, checkout);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        checkout.SelectedCustomer = await CustomersService().GetByIdAsync("C");
        checkout.RedeemLoyaltyPointsCommand.Execute(null);
        checkout.ReceivedAmountInput = "15";
        await ClickTwiceAsync(view, checkout.ConfirmPaymentCommand);
        Assert.Equal("", checkout.ErrorMessage);
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(5m, (await db.SalePayments.SingleAsync(p => p.Kind == TenderKind.Loyalty)).Amount);
        Assert.Equal(15m, (await db.Shifts.AsNoTracking().SingleAsync()).ExpectedBalance);
        Assert.Equal(1, printer.Calls);
        Assert.True(outcome!.IsError);
        Assert.Contains("No vuelva a cobrar", outcome.Text);
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(receiver);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ui_EditPromotion_PreservesRuleIdentityAndUpdatesPosPrice()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var rule = new PromotionDto("EDIT-PROMO", "Volumen", 8m, true, Ticketfy.Core.Enums.PromotionType.VolumeTier,
            TargetProductId: "P", MinQuantity: 3, StartDate: Ticketfy.Core.Helpers.BusinessTime.Today, EndDate: Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(2));
        await service.SaveAsync(rule, "CREATE-EDIT-PROMO");
        rule = Assert.Single(await service.GetAllAsync());
        var vm = new PromotionDialogViewModel(service, ProductsService(), rule);
        var view = new PromotionDialog(); var window = Show(view, vm);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            vm.SelectedProduct = await ProductsService().GetByIdAsync("P");
            Assert.Equal(3, vm.SelectedTypeIndex); Assert.Equal(3m, vm.MinimumQuantity);
            vm.DiscountValueText = "7"; vm.MinimumQuantity = 2;
            await ClickTwiceAsync(view, vm.SaveCommand);
            Assert.Equal("", vm.ErrorMessage);
            var saved = Assert.Single(await service.GetAllAsync());
            Assert.Equal("EDIT-PROMO", saved.Id); Assert.Equal(7m, saved.DiscountValue); Assert.Equal(2d, saved.MinQuantity);
            var cart = await service.EvaluateAndApplyPromotionsAsync(UiCart());
            Assert.Equal(6m, cart.Single().AppliedDiscountAmount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_CompletedCashMovementAndWalletLoad_ResetAmountsBeforeAnotherClick()
    {
        var factory = new TestDbContextFactory(_options);
        await using var db = Db();
        var vm = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor),
            new PurchaseService(factory, _actor), new ShiftService(factory, _actor), new GiftcardService(db, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor, Sales);
        var window = Show(new CommercialControlView(), vm);
        try
        {
            vm.Amount = 30m; vm.Reason = "Ingreso documentado"; vm.IsOutflow = false;
            await vm.CashMovementCommand.ExecuteAsync(null);
            Assert.Equal(0m, vm.Amount);
            await vm.CashMovementCommand.ExecuteAsync(null);
            Assert.Single(await db.ShiftMovements.ToListAsync());
            vm.WalletNumber = "WALLET"; vm.Amount = 20m;
            await vm.LoadWalletCommand.ExecuteAsync(null);
            Assert.Equal(0m, vm.Amount);
            await vm.LoadWalletCommand.ExecuteAsync(null);
            Assert.Equal(120m, (await db.Giftcards.AsNoTracking().SingleAsync()).Balance);
            Assert.Equal(2, await db.ShiftMovements.CountAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_CreditSaleAndAbono_OnlyAbonoEntersDrawer()
    {
        var checkout = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), new EvidenceTerminal(this), UiCart(), 20m);
        var view = new CheckoutDialog(); var window = Show(view, checkout);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        checkout.SelectedCustomer = await CustomersService().GetByIdAsync("C");
        checkout.PaymentMethod = "Crédito de Cliente";
        Assert.True(checkout.IsCreditPayment);
        await ClickTwiceAsync(view, checkout.ConfirmPaymentCommand);
        Assert.Equal("", checkout.ErrorMessage);
        Assert.Equal(20m, (await CustomersService().GetStatementAsync("C")).Balance);
        window.Close();
        var abono = new PaymentDialogViewModel(CustomersService(), "C", 20m) { Amount = 10m };
        var abonoView = new PaymentDialog(); window = Show(abonoView, abono);
        await ClickTwiceAsync(abonoView, abono.SaveCommand);
        Assert.Equal("", abono.ErrorMessage);
        Assert.Equal(10m, (await CustomersService().GetStatementAsync("C")).Balance);
        await using var db = Db();
        Assert.Single(await db.CustomerPayments.ToListAsync());
        Assert.Equal(10m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ui_PurchaseControlKardexAndCashup_UseTheSameLedgers()
    {
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        var purchases = new PurchaseService(factory, _actor);
        var suppliers = new SupplierService(factory, _actor);
        var shifts = new ShiftService(factory, _actor);
        await shifts.RegisterMovementAsync(100m, false, "Fondo extraordinario", "FLOAT");
        var purchase = new PurchaseOrderViewModel(purchases, ProductsService());
        await purchase.LoadCatalogAsync(await suppliers.GetAllAsync());
        purchase.SelectedSupplier = purchase.Suppliers.Single();
        purchase.SelectedProduct = purchase.AvailableProducts.Single();
        purchase.InvoiceNumber = "UI-INVOICE"; purchase.PurchaseQuantity = "2";
        purchase.PurchasePaymentMethod = "Efectivo"; purchase.PurchasePaidAmount = 6m;
        purchase.AddItemToDraftCommand.Execute(null);
        var purchaseView = new PurchaseOrderView(); var window = Show(purchaseView, purchase);
        await ClickTwiceAsync(purchaseView, purchase.ConfirmPurchaseCommand);
        Assert.Single(await db.Purchases.ToListAsync());
        Assert.Equal(0m, (await purchases.GetStatementAsync("S")).Balance);
        window.Close();
        var control = new CommercialControlViewModel(ProductsService(), CustomersService(), suppliers, purchases, shifts,
            new GiftcardService(factory, _actor), new CommercialReconciliationService(factory, _actor), factory, _actor);
        var controlView = new CommercialControlView(); window = Show(controlView, control);
        await control.RefreshCommand.ExecuteAsync(null);
        control.Product = control.Products.Single();
        await control.KardexCommand.ExecuteAsync(null);
        Assert.Single(control.Rows);
        Assert.Equal(22m, control.Rows[0].Balance);
        window.Close();
        var count = new CashupDialogViewModel(shifts, isFinalZCut: true) { Count50 = "1", Count20 = "2", Count2 = "2" };
        var countView = new CashupDialog(); window = Show(countView, count);
        await ClickTwiceAsync(countView, count.SaveCashupCommand);
        Assert.False(count.IsFeedbackError, count.FeedbackMessage);
        var closed = await db.Shifts.AsNoTracking().SingleAsync();
        Assert.Equal(0, closed.IsOpen); Assert.Equal(94m, closed.ExpectedBalance); Assert.Equal(0m, closed.Diff);
        Assert.Single(await db.Cashups.ToListAsync());
        window.Close();
    }
}
