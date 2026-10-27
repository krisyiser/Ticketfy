using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task Ui_ControlCatalogSearchIsBoundedAndFindsRecordsOutsideFirstHundred()
    {
        await using var db = Db();
        for (int i = 0; i < 150; i++)
        {
            string label = "Lookup " + i.ToString("D3");
            db.Customers.Add(new CustomerEntity { Id = "LOOKUP-C" + i, Name = label, CustomerCode = "C" + i });
            db.Suppliers.Add(new SupplierEntity { Id = "LOOKUP-S" + i, Name = label });
            db.Products.Add(new ProductEntity { Id = "LOOKUP-P" + i, Name = label, Barcode = "P" + i });
        }
        await db.SaveChangesAsync();
        var model = EnterpriseControl();
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(100, model.Customers.Count); Assert.Equal(100, model.Suppliers.Count); Assert.Equal(100, model.Products.Count);
            model.Customer = model.Customers[0]; model.CreditActionVerified = true;
            model.CatalogSearch = "Lookup 149";
            await ClickTwiceAsync(view, model.SearchCatalogsCommand);
            Assert.Equal("LOOKUP-C149", Assert.Single(model.Customers).Id);
            Assert.Equal("LOOKUP-S149", Assert.Single(model.Suppliers).Id);
            Assert.Equal("LOOKUP-P149", Assert.Single(model.Products).Id);
            Assert.Null(model.Customer); Assert.False(model.CreditActionVerified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_SupervisorRecoversAnotherCashierWithoutBankPermissionsOrOwnShift()
    {
        await BeginCashierRecoveryAsync("UI-SUPERVISED");
        await using var db = Db();
        var supervisor = CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "SUPERVISOR");
        var factory = new TestDbContextFactory(_options);
        var model = new CommercialControlViewModel(new ProductService(factory, supervisor), new CustomerService(factory, supervisor),
            new SupplierService(factory, supervisor), new PurchaseService(factory, supervisor), new ShiftService(factory, supervisor),
            new GiftcardService(factory, supervisor), new CommercialReconciliationService(factory, supervisor), factory, supervisor, SalesFor(supervisor));
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(model.CanRecoverCheckout); Assert.False(model.CanTransfers);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Recuperar cobros"));
            model.PendingCheckout = Assert.Single(model.PendingCheckouts);
            model.Reason = "Conteo comprobado por supervisor"; model.CheckoutFundsVerified = true;
            await ClickTwiceAsync(view, model.ResolveCheckoutCommand);
            Assert.Empty(model.PendingCheckouts);
            Assert.Equal(1, await db.Sales.CountAsync());
            Assert.Equal(1, await db.SalePayments.CountAsync());
            Assert.Equal(CommercialTestSession.UserId.ToString(), (await db.Sales.SingleAsync()).CashierUserId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_SupplierCreditRefundConfirmsReceiptOnceAndClearsPendingRequest()
    {
        await new ShiftService(new TestDbContextFactory(_options), _actor).RegisterMovementAsync(100m, false, "Fondo", "UI-SUPPLIER-FUND");
        await PaidSupplierReturnAsync();
        var model = EnterpriseControl();
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
            model.CreditSupplier = true; model.Supplier = Assert.Single(model.Suppliers);
            await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
            model.CreditSource = Assert.Single(model.CreditSources);
            model.Amount = 8m; model.Method = "Efectivo"; model.Reason = "Reembolso recibido"; model.CreditActionVerified = true;
            await ClickTwiceAsync(view, model.PrepareAccountCreditRefundCommand);
            model.PendingCreditRefund = Assert.Single(model.PendingCreditRefunds); model.CreditRefundFundsVerified = true;
            await ClickTwiceAsync(view, model.RefundAccountCreditCommand);
            Assert.Empty(model.PendingCreditRefunds);
            await using var db = Db();
            Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
            Assert.Equal(96m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Equal(0m, (await new PurchaseService(new TestDbContextFactory(_options), _actor).GetStatementAsync("S")).Balance);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_AttendanceReviewRequiresEvidenceAndKeepsOneResolution()
    {
        await using var db = Db();
        (await db.Shifts.SingleAsync()).IsOpen = 0;
        DateTime start = Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-3).AddHours(8);
        db.Attendances.Add(new AttendanceEntity
        {
            Id = "UI-ATTENDANCE",
            UserId = CommercialTestSession.UserId,
            CheckInTime = start,
            Status = Ticketfy.Core.Enums.AttendanceStatus.IncompleteAnomaly
        });
        await db.SaveChangesAsync();
        var model = EnterpriseControl();
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Asistencias"));
            await model.ReviewAttendancesCommand.ExecuteAsync(null);
            model.AttendanceReview = Assert.Single(model.AttendanceReviews);
            model.ConfirmedAttendanceEnd = start.AddHours(4).ToString("yyyy-MM-dd HH:mm");
            model.Reason = "Salida comprobada con registro del supervisor";
            await model.ResolveAttendanceCommand.ExecuteAsync(null);
            Assert.Contains("confirme", model.Message);
            model.AttendanceEvidenceVerified = true;
            await ClickTwiceAsync(view, model.ResolveAttendanceCommand);
            Assert.Empty(model.AttendanceReviews);
            Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "ATTENDANCE_REVIEWED"));
        }
        finally { window.Close(); }
    }

    private CommercialControlViewModel EnterpriseControl(Ticketfy.Services.Interfaces.IPaymentTerminalReconciliation? terminal = null)
    {
        var factory = new TestDbContextFactory(_options);
        return new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor),
            new PurchaseService(factory, _actor), new ShiftService(factory, _actor), new GiftcardService(factory, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor, Sales, terminal);
    }

    [AvaloniaFact]
    public async Task Ui_CustomerCreditRefund_UsesVerifiedBoundButtonAndDoubleClickCannotRefundAgain()
    {
        await PaidCustomerReturnAsync();
        var model = EnterpriseControl();
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(model.CanSettleCredit);
            view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
            model.Customer = model.Customers.Single();
            await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
            model.CreditSource = model.CreditSources.Single();
            model.Amount = 10m; model.Method = "Efectivo"; model.Reason = "Efectivo entregado";
            model.CreditActionVerified = true;
            await ClickTwiceAsync(view, model.PrepareAccountCreditRefundCommand);
            Assert.False(model.CreditActionVerified);
            model.PendingCreditRefund = model.PendingCreditRefunds.Single();
            model.CreditRefundFundsVerified = true;
            await ClickTwiceAsync(view, model.RefundAccountCreditCommand);
            Assert.False(model.CreditActionVerified);
            Assert.Empty(model.CreditSources);
            await using var db = Db();
            Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
            Assert.Equal(0m, (await db.Customers.SingleAsync()).Debt);
            Assert.Equal(10m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_CreditApplication_RequiresNewConfirmationWhenAccountChanges()
    {
        await PaidCustomerReturnAsync();
        var target = await Sales.SaveAsync(Sale("UI-APPLY-TARGET") with { IsCredit = true, CustomerId = "C", CashAmount = 0, PaymentMethod = "Crédito" });
        var model = EnterpriseControl();
        var view = new CommercialControlView();
        var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
            model.Customer = model.Customers.Single();
            await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
            model.CreditSource = model.CreditSources.Single();
            model.CreditTarget = model.CreditTargets.Single();
            Assert.Equal(target.Id, model.CreditTarget.DocumentId);
            model.Amount = 10m; model.Reason = "Compensar deuda"; model.CreditActionVerified = true;
            await ClickTwiceAsync(view, model.ApplyAccountCreditCommand);
            await using var db = Db();
            Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
            Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Equal(10m, (await db.Customers.SingleAsync()).Debt);
            model.CreditActionVerified = true;
            model.CreditSupplier = true;
            Assert.False(model.CreditActionVerified);
            Assert.Null(model.CreditSource);
            Assert.Null(model.CreditTarget);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PurchasePages_AreBoundedStableAndSearchAllHistory()
    {
        await using var db = Db();
        for (int i = 0; i < 225; i++)
            db.Purchases.Add(new PurchaseEntity
            {
                Id = "PAGE-" + i.ToString("D4"),
                SupplierId = "S",
                SupplierName = "Proveedor",
                InvoiceNumber = "FACTURA-" + i.ToString("D4"),
                Date = "2026-10-01T00:00:00Z",
                TotalCost = 5m
            });
        await db.SaveChangesAsync();
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var first = await service.GetPageAsync(0);
        var second = await service.GetPageAsync(1);
        var last = await service.GetPageAsync(2);
        Assert.Equal(100, first.Count); Assert.Equal(100, second.Count); Assert.Equal(25, last.Count);
        Assert.Equal(225, first.Concat(second).Concat(last).Select(p => p.Id).Distinct().Count());
        Assert.Equal("PAGE-0224", Assert.Single(await service.GetPageAsync(0, search: "factura-0224")).Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(-1));
    }
}
