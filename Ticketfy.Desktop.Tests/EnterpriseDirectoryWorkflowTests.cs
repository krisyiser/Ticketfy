using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Suppliers;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;
using Ticketfy.Views.Suppliers;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task SeedDirectoryPagesAsync()
    {
        await using var db = Db();
        for (var i = 0; i < 215; ++i)
        {
            db.Customers.Add(new CustomerEntity { Id = $"DC-{i:D3}", Name = $"Directory {i:D3}", CustomerCode = $"LOOK-{i:D3}", Email = $"customer{i:D3}@example.invalid" });
            db.Suppliers.Add(new SupplierEntity { Id = $"DS-{i:D3}", Name = $"Directory {i:D3}", ContactPerson = $"Contact {i:D3}" });
            db.Products.Add(new ProductEntity { Id = $"DP-{i:D3}", Name = $"Directory {i:D3}", Barcode = $"BAR-{i:D3}", Price = 15m, Cost = 4m, Stock = 10d, DefaultSupplierId = $"DS-{i:D3}" });
        }
        db.Products.Add(new ProductEntity { Id = "DIRECTORY-KIT", Name = "Directory kit", Barcode = "DIRECTORY-KIT", IsKit = true });
        await db.SaveChangesAsync();
    }

    [AvaloniaFact]
    public async Task Ui_DirectoriesPageSearchAllRecordsAndDiscardDataOnLogout()
    {
        await SeedDirectoryPagesAsync();
        var customers = new CustomersViewModel(CustomersService());
        var customerView = new CustomersView();
        var customerWindow = Show(customerView, customers);
        await using var db = Db();
        var suppliers = new SupplierDirectoryViewModel(new SupplierService(db, _actor));
        var supplierView = new SuppliersDirectoryView();
        var supplierWindow = Show(supplierView, suppliers);
        try
        {
            await customers.LoadCustomersAsync();
            await suppliers.LoadSuppliersAsync();
            Assert.Equal(100, customers.Customers.Count);
            Assert.Equal(100, suppliers.Suppliers.Count);
            // Exercise bound UI commands without the double-click helper advancing twice.
            await customers.NextPageCommand.ExecuteAsync(null);
            await suppliers.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(100, customers.Customers.Count);
            Assert.Equal(100, suppliers.Suppliers.Count);
            await customers.NextPageCommand.ExecuteAsync(null);
            await suppliers.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(16, customers.Customers.Count);
            Assert.Equal(16, suppliers.Suppliers.Count);
            Assert.False(customers.HasNextPage);
            Assert.False(suppliers.HasNextPage);
            customers.SearchQuery = "customer214@example.invalid";
            suppliers.SearchQuery = "Contact 214";
            await customers.LoadCustomersAsync();
            await suppliers.LoadSuppliersAsync();
            Assert.Equal("DC-214", Assert.Single(customers.Customers).Id);
            Assert.Equal("DS-214", Assert.Single(suppliers.Suppliers).Id);
            customers.SearchQuery = "pending"; suppliers.SearchQuery = "pending";
            customers.ClearSessionData(); suppliers.ClearSessionData();
            await Task.Delay(220);
            Assert.Empty(customers.Customers); Assert.Empty(suppliers.Suppliers);
        }
        finally { customerWindow.Close(); supplierWindow.Close(); }
    }

    [Fact]
    public async Task DirectoryPages_ValidateBoundsAndFilterPurchasableProductsBeforeLimit()
    {
        await SeedDirectoryPagesAsync();
        await using var db = Db();
        var customers = CustomersService(); var suppliers = new SupplierService(db, _actor); var products = ProductsService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => customers.GetPageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => suppliers.GetPageAsync(0, 201));
        await Assert.ThrowsAsync<OverflowException>(() => customers.GetPageAsync(int.MaxValue));
        Assert.Equal("DP-214", Assert.Single((await products.GetPurchasePageAsync(0, supplierId: "DS-214")).Items).Id);
        Assert.Empty((await products.GetPurchasePageAsync(0, search: "DIRECTORY-KIT")).Items);
        (await db.Customers.SingleAsync(c => c.Id == "DC-214")).IsActive = false;
        (await db.Suppliers.SingleAsync(s => s.Id == "DS-214")).IsActive = 0;
        await db.SaveChangesAsync();
        Assert.Empty((await customers.GetPageAsync(0, search: "LOOK-214")).Items);
        Assert.Empty((await suppliers.GetPageAsync(0, search: "Contact 214")).Items);
    }

    [AvaloniaFact]
    public async Task Ui_PurchaseSearchFindsUnloadedSupplierAndProductAndResetsPaymentAfterCommit()
    {
        await SeedDirectoryPagesAsync();
        await using var db = Db();
        var model = new PurchaseOrderViewModel(new PurchaseService(new TestDbContextFactory(_options), _actor), ProductsService(), new SupplierService(db, _actor));
        var view = new PurchaseOrderView();
        var window = Show(view, model);
        try
        {
            await model.LoadCatalogAsync();
            Assert.Equal(100, model.Suppliers.Count); Assert.Equal(100, model.AvailableProducts.Count);
            model.SupplierSearch = "Contact 214";
            await ClickTwiceAsync(view, model.SearchSuppliersCommand);
            model.SelectedSupplier = Assert.Single(model.Suppliers);
            model.PreferredSupplierOnly = true;
            await model.SearchProductsCommand.ExecuteAsync(null);
            model.SelectedProduct = Assert.Single(model.AvailableProducts);
            Assert.Equal("DP-214", model.SelectedProduct.Id);
            model.PurchaseQuantity = "NaN";
            model.AddItemToPurchaseDraftCommand.Execute(null);
            Assert.Empty(model.DraftPurchaseItems);
            model.PurchaseQuantity = "2"; model.PurchaseUnitPrice = "4";
            model.AddItemToPurchaseDraftCommand.Execute(null);
            model.InvoiceNumber = "DIRECTORY-INVOICE";
            model.PurchasePaymentMethod = "Efectivo"; model.PurchasePaidAmount = 8m;
            model.PurchasePaymentReference = "synthetic-note";
            await ClickTwiceAsync(view, model.ConfirmPurchaseCommand);
            Assert.Single(await db.Purchases.ToListAsync());
            Assert.Single(await db.SupplierPayments.ToListAsync());
            Assert.Equal(12d, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == "DP-214")).Stock);
            Assert.Equal(0m, model.PurchasePaidAmount);
            Assert.Empty(model.PurchasePaymentReference);
            Assert.Equal("Crédito", model.PurchasePaymentMethod);
            Assert.Empty(model.DraftPurchaseItems);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_PromotionSearchPreservesInitialTargetOutsideFirstPageAndKitUsesBoundedSearch()
    {
        await SeedDirectoryPagesAsync();
        await using var db = Db();
        var promotions = new PromotionService(db, _actor);
        var initial = new PromotionDto("PROMO-DIRECTORY", "Synthetic targeted", 10m, true, PromotionType.PercentageDiscount, "DP-214", "",
            StartDate: Ticketfy.Core.Helpers.BusinessTime.Today, EndDate: Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(1));
        await promotions.SaveAsync(initial, "CREATE-DIRECTORY-PROMO");
        initial = Assert.Single(await promotions.GetAllAsync());
        var promotion = new PromotionDialogViewModel(promotions, ProductsService(), initial);
        var promotionView = new PromotionDialog(); var promotionWindow = Show(promotionView, promotion);
        var kit = new ItemKitDialogViewModel(new ItemKitService(db, _actor), ProductsService());
        var kitView = new ItemKitDialog(); var kitWindow = Show(kitView, kit);
        try
        {
            await promotion.SearchProductsCommand.ExecuteAsync(null);
            Assert.Equal("DP-214", promotion.SelectedProduct?.Id);
            Assert.Equal(101, promotion.Products.Count); // 100 matches plus pinned current target.
            promotion.ProductSearch = "BAR-001";
            await ClickTwiceAsync(promotionView, promotion.SearchProductsCommand);
            Assert.Equal("DP-214", promotion.SelectedProduct?.Id);
            Assert.Equal(2, promotion.Products.Count);
            await ClickTwiceAsync(promotionView, promotion.SaveCommand);
            Assert.Equal("DP-214", (await db.Promotions.SingleAsync()).TargetProductId);
            kit.ProductSearch = "BAR-214";
            await ClickTwiceAsync(kitView, kit.LoadProductsCommand);
            kit.SelectedProduct = Assert.Single(kit.AvailableProducts);
            kit.ItemQuantity = 2d; kit.AddItemToDraftCommand.Execute(null);
            kit.KitName = "Synthetic kit"; kit.KitBarcode = "SYNTHETIC-KIT"; kit.KitPrice = 25m;
            await ClickTwiceAsync(kitView, kit.SaveKitCommand);
            Assert.Single(await db.ItemKits.Where(k => k.KitBarcode == "SYNTHETIC-KIT").ToListAsync());
            Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "KIT_SAVED").ToListAsync());
        }
        finally { promotionWindow.Close(); kitWindow.Close(); }
    }

    [AvaloniaFact]
    public async Task Checkout_SelectedCustomerIsAssignedSynchronouslyAndNeverLoadsFullDirectory()
    {
        var selected = new CustomerDto("C", "Synthetic customer", "", "", "", 100m);
        var customers = new DeferredCustomers(Task.FromException<List<CustomerDto>>(new InvalidOperationException("Checkout must not query catalog.")));
        var model = new CheckoutDialogViewModel(Sales, customers, new TestPrintDispatcher(), new EvidenceTerminal(this), UiCart(), 20m, preselectedCustomer: selected);
        Assert.Same(selected, model.SelectedCustomer);
        model.AppliedTenders.Add(new TenderEntryModel { MethodName = "Efectivo", AmountPaid = 10m });
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Same(selected, model.SelectedCustomer);
        Assert.Single(model.AppliedTenders);
    }

    [Fact]
    public async Task KitCreateWithExistingBarcodeOrFractionalPiece_CannotRewriteExistingRecipe()
    {
        await using var db = Db();
        var service = new ItemKitService(db, _actor);
        await service.SaveAsync(new("ORIGINAL-KIT", "SHARED-CODE", "Original", 10m, "", [new("", "", "P", "Producto", 1d)]), "CREATE");
        await Assert.ThrowsAsync<Ticketfy.Services.KitCommandRejectedException>(() => service.SaveAsync(
            new("OTHER-KIT", "SHARED-CODE", "Unexpected replacement", 99m, "", [new("", "", "P", "Producto", 2d)]), "DUPLICATE"));
        await Assert.ThrowsAsync<Ticketfy.Services.KitCommandRejectedException>(() => service.SaveAsync(
            new("FRACTION-KIT", "FRACTION-CODE", "Fractional piece", 10m, "", [new("", "", "P", "Producto", .5d)]), "FRACTION"));
        Assert.Equal("Original", (await service.GetByBarcodeAsync("SHARED-CODE"))!.Name);
        Assert.Single(await db.ItemKits.ToListAsync());
        Assert.Equal(1d, (await db.ItemKitItems.SingleAsync()).Quantity);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "KIT_SAVED").ToListAsync());
    }
}
