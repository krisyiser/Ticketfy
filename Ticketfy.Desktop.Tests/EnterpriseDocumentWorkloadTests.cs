using System.Text;
using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Core.State;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Suppliers;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task DocumentWorkload_ExactlyOneThousandSaleLinesCommitAndReplayOnce()
    {
        await using var db = Db(); (await db.Products.SingleAsync()).Stock = 5000; await db.SaveChangesAsync();
        var source = Sale("MAXIMUM-SALE");
        var sale = source with { Items = Enumerable.Repeat(source.Items[0], 1000).ToList(), Total = 20000m, PaidAmount = 20000m, CashAmount = 20000m };
        await Sales.BeginCheckoutAsync(sale); var saved = await Sales.SaveAsync(sale);
        Assert.Equal(saved.Id, (await Sales.SaveAsync(sale)).Id);
        Assert.Equal(1000, saved.Items.Count); Assert.Equal(1000, await db.SaleLines.CountAsync());
        Assert.Equal(20000m, (await db.SalePayments.SingleAsync()).Amount);
        Assert.Equal(6000m, (await db.Sales.SingleAsync()).TotalCost);
        Assert.Equal(3000d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
        Assert.Equal(1000, await db.InventoryMovements.CountAsync());
        Assert.Equal(CheckoutOperationState.Completed, (await db.CheckoutOperations.SingleAsync()).State);
    }

    [Fact]
    public async Task DocumentWorkload_ExactlyOneThousandPurchaseLinesCommitAndReplayOnce()
    {
        var purchase = new PurchaseDto("MAXIMUM-BUY", "S", "Proveedor", "MAXIMUM-F", "", 0m, new string('n', 2000),
            Enumerable.Range(0, 1000).Select(i => new PurchaseItemDto("L" + i, "", "P", "Producto", 4m, 1, 4m)).ToList());
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var saved = await service.RegisterPurchaseAsync(purchase);
        Assert.Equal(saved.Id, (await service.RegisterPurchaseAsync(purchase)).Id);
        await using var db = Db();
        Assert.Equal(1000, await db.PurchaseItems.CountAsync()); Assert.Equal(4000m, (await db.AccountEntries.SingleAsync()).Amount);
        Assert.Equal(1020d, (await db.Products.SingleAsync()).Stock); Assert.Equal(4m, (await db.Products.SingleAsync()).Cost);
        Assert.Equal(1000, await db.InventoryMovements.CountAsync());
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task DocumentWorkload_RepeatedCombosCannotBypassTheInventoryPostingBudget(int rows, bool allowed)
    {
        await using var db = Db();
        var kit = new ItemKitEntity { Id = "BUDGET-KIT", ParentProductId = "BUDGET-PARENT", KitBarcode = "BUDGET", Name = "Combo", Price = 200m };
        db.Products.Add(new ProductEntity { Id = "BUDGET-PARENT", Barcode = "BUDGET", Name = "Combo", IsKit = true, Unit = "Pza", Price = 200m });
        for (int i = 0; i < 200; i++)
        {
            string id = "INGREDIENT-" + i;
            db.Products.Add(new ProductEntity { Id = id, Barcode = id, Name = id, Unit = "Pza", Cost = 1m, Stock = 100 });
            kit.Components.Add(new ItemKitItemEntity { ItemKitId = kit.Id, ProductId = id, Quantity = 1 });
        }
        db.ItemKits.Add(kit); await db.SaveChangesAsync();
        var line = Sale("COMBO-BUDGET").Items[0] with
        { ProductId = "BUDGET-PARENT", UnitPrice = 200m, OriginalUnitPrice = 200m, Quantity = 1, TotalPrice = 200m };
        var sale = Sale("COMBO-BUDGET") with
        { Items = Enumerable.Repeat(line, rows).ToList(), Total = rows * 200m, PaidAmount = rows * 200m, CashAmount = rows * 200m };
        if (allowed) await Sales.BeginCheckoutAsync(sale);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(sale));
        Assert.Equal(allowed ? 1 : 0, await db.CheckoutOperations.CountAsync());
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Equal(100d, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == "INGREDIENT-0")).Stock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentWorkload_CanonicalCatalogTextIsCheckedBeforeStockOrIntent(bool purchase)
    {
        await using var db = Db(); (await db.Products.SingleAsync()).Name = new string('x', 1025); await db.SaveChangesAsync();
        if (purchase)
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor)
                .RegisterPurchaseAsync(new("CANONICAL", "S", "Proveedor", "F", "", 0m, "", [new("L", "", "P", "small client text", 4m, 1, 4m)])));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("CANONICAL")));
        Assert.Empty(await db.CheckoutOperations.ToListAsync()); Assert.Empty(await db.Purchases.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync()); Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentWorkload_NulInputCannotBeSilentlyShortenedByStorage(bool purchase)
    {
        if (purchase)
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor)
                .RegisterPurchaseAsync(new("NUL-BUY", "S", "Proveedor", "F", "", 0m, "before\0after", [new("L", "", "P", "Producto", 4m, 1, 4m)])));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("NUL\0CHECKOUT")));
        await using var db = Db();
        Assert.Empty(await db.CheckoutOperations.ToListAsync()); Assert.Empty(await db.Purchases.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync()); Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null-line")]
    [InlineData("excess")]
    public async Task DocumentWorkload_CompatibilityCheckoutReturnsAValidationResultBeforeCopying(string kind)
    {
        List<CartItemDto> items = kind switch
        {
            "missing" => null!,
            "null-line" => [null!],
            _ => Enumerable.Repeat(UiCart()[0], 1001).ToList()
        };
        var result = await Sales.ProcessSaleAsync(new(null, items, 20m, 0m, PaymentMethod.Efectivo, OperationId: "COMPAT-LIMIT"));
        Assert.False(result.IsSuccess); Assert.NotEmpty(result.ErrorMessage);
        await using var db = Db();
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    [Fact]
    public async Task DocumentWorkload_CombinedUnicodeTextAndEscapedJsonHaveSeparateBudgets()
    {
        var source = Sale("TEXT-BUDGET");
        var unicode = source with { Items = Enumerable.Repeat(source.Items[0] with { Name = new string('é', 1024) }, 500).ToList() };
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.ValidateSale(unicode));
        var valid = source with { Items = [source.Items[0] with { Name = new string('é', 1024) }] };
        CommercialDocumentLimits.ValidateSale(valid);
        await using var db = Db();
        var product = await db.Products.SingleAsync(); product.Name = new string('<', 700); product.Stock = 2000; await db.SaveChangesAsync();
        var escaping = source with { Items = Enumerable.Repeat(source.Items[0], 400).ToList(), Total = 8000m, PaidAmount = 8000m, CashAmount = 8000m };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(escaping));
        Assert.Empty(await db.CheckoutOperations.ToListAsync()); Assert.Empty(await db.Sales.ToListAsync());
    }

    [Fact]
    public void DocumentWorkload_ByteLimitsAreExactAndRejectExcessAndNullTenders()
    {
        Assert.Equal(CommercialDocumentLimits.PayloadBytes, Encoding.UTF8.GetByteCount(
            CommercialDocumentLimits.RequirePayload(new string('é', CommercialDocumentLimits.PayloadBytes / 2))));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.RequirePayload(new string('é', CommercialDocumentLimits.PayloadBytes / 2 + 1)));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.RequirePayload(new string('x', CommercialDocumentLimits.PayloadBytes + 1)));
        Assert.Equal(CommercialDocumentLimits.NewPayloadBytes, CommercialDocumentLimits.RequireNewPayload(new string('x', CommercialDocumentLimits.NewPayloadBytes)).Length);
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.RequireNewPayload(new string('é', CommercialDocumentLimits.NewPayloadBytes)));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.ValidateSale(Sale("TENDERS") with { Payments = [null!] }));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.ValidateSale(Sale("TENDERS") with
        { Payments = Enumerable.Repeat(new SaleTenderDto(TenderKind.Cash, 1m), 7).ToList() }));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.ValidateSale(Sale("EMPTY") with { Items = [] }));
        Assert.Throws<InvalidOperationException>(() => CommercialDocumentLimits.ReadSale("null"));
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("unicode")]
    [InlineData("fiscal")]
    public async Task DocumentWorkload_OversizedPayloadNeverMaterializesOrChangesDuringRecovery(string kind)
    {
        await PrepareTerminalCheckout();
        string huge = kind == "nul" ? "\0" + new string('x', CommercialDocumentLimits.PayloadBytes)
            : new string('é', CommercialDocumentLimits.PayloadBytes);
        string originalSale, originalFiscal;
        var baseOptions = _options;
        await using (var db = Db())
        {
            var row = await db.CheckoutOperations.SingleAsync();
            if (kind == "fiscal") row.FiscalPayload = huge; else row.SalePayload = huge;
            originalSale = row.SalePayload; originalFiscal = row.FiscalPayload ?? "";
            CommercialTestSession.ConfirmCard(db, "BANK-EVIDENCE", 20m);
            await db.SaveChangesAsync();
            // Bind bytes to create actual corrupt storage, independent of text-provider NUL conversion.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE checkout_operations SET sale_payload = CAST({Encoding.UTF8.GetBytes(originalSale)} AS TEXT)");
            Assert.Equal(Encoding.UTF8.GetBytes(originalSale), await db.Database.SqlQueryRaw<byte[]>(
                "SELECT CAST(sale_payload AS BLOB) AS Value FROM checkout_operations").SingleAsync());
        }
        var observer = new DocumentMaterializationProbe();
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.True(observer.Count > 0); Assert.False(observer.Oversize);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "Sin pruebas suficientes"));
        _options = baseOptions;
        await using var verify = Db(); var retained = await verify.CheckoutOperations.SingleAsync();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, retained.State);
        // Verify raw storage bytes, so text-provider NUL conversion cannot hide a change.
        Assert.Equal(Encoding.UTF8.GetBytes(originalSale), await verify.Database.SqlQueryRaw<byte[]>(
            "SELECT CAST(sale_payload AS BLOB) AS Value FROM checkout_operations").SingleAsync());
        Assert.Equal(Encoding.UTF8.GetBytes(originalFiscal), await verify.Database.SqlQueryRaw<byte[]>(
            "SELECT CAST(COALESCE(\"FiscalPayload\", '') AS BLOB) AS Value FROM checkout_operations").SingleAsync());
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task DocumentWorkload_EmbeddedNulCannotHideTrailingCorruptionBehindAValidSale()
    {
        await PrepareTerminalCheckout();
        byte[] original;
        await using (var db = Db())
        {
            var row = await db.CheckoutOperations.SingleAsync();
            row.SalePayload += "\0trailing invalid JSON";
            original = Encoding.UTF8.GetBytes(row.SalePayload);
            CommercialTestSession.ConfirmCard(db, "BANK-EVIDENCE", 20m);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE checkout_operations SET sale_payload = CAST({original} AS TEXT)");
            Assert.Equal(original, await db.Database.SqlQueryRaw<byte[]>(
                "SELECT CAST(sale_payload AS BLOB) AS Value FROM checkout_operations").SingleAsync());
        }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Equal(original, await verify.Database.SqlQueryRaw<byte[]>(
            "SELECT CAST(sale_payload AS BLOB) AS Value FROM checkout_operations").SingleAsync());
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task DocumentWorkload_TerminalCannotSendAnOrderFromAnOversizedHistoricalIntent()
    {
        await PrepareTerminalCheckout();
        await using (var db = Db())
        {
            (await db.CheckoutOperations.SingleAsync()).SalePayload = new string(' ', CommercialDocumentLimits.PayloadBytes + 1);
            await db.SaveChangesAsync();
        }
        var handler = new EvidenceHttpHandler(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var client = new HttpClient(handler);
        var terminal = BankTerminal(client);
        Assert.False((await terminal.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Empty(handler.Methods);
        await using var verify = Db(); Assert.Empty(await verify.PaymentTerminalOperations.ToListAsync());
        Assert.Single((await Sales.GetInterruptedCheckoutsPageAsync()).Items);
    }

    [Fact]
    public async Task DocumentWorkload_RecognizingAnAlreadyCommittedSaleDoesNotReadItsHistoricalJson()
    {
        await Sales.BeginCheckoutAsync(Sale("KNOWN-COMMIT"));
        await Sales.SaveAsync(Sale("KNOWN-COMMIT"));
        string historical = new string(' ', CommercialDocumentLimits.PayloadBytes + 1);
        await using (var db = Db())
        {
            (await db.CheckoutOperations.SingleAsync()).State = CheckoutOperationState.Started;
            (await db.Sales.SingleAsync()).ItemsJson = historical;
            await db.SaveChangesAsync();
        }
        var baseOptions = _options;
        var observer = new DocumentMaterializationProbe();
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, observer.Sales);
        _options = baseOptions;
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Equal(historical, (await verify.Sales.SingleAsync()).ItemsJson);
        Assert.Single(await verify.SalePayments.ToListAsync()); Assert.Single(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
    }

    [AvaloniaFact]
    public async Task DocumentWorkload_CheckoutUiRejectsBeforeBankAndPreservesEditableCart()
    {
        var terminal = new CheckoutFailureTerminal((_, _, _) => throw new InvalidOperationException("No bank call permitted"));
        var cart = Enumerable.Range(0, 1001).Select(_ => UiCart()[0]).ToList();
        var print = new TestPrintDispatcher();
        var model = new CheckoutDialogViewModel(Sales, CustomersService(), print, terminal, cart, 20020m)
        { PaymentMethod = "Tarjeta", ReceivedAmount = 20020m };
        await model.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Contains("1.000 partidas", model.ErrorMessage); Assert.False(model.RequiresReconciliation);
        Assert.True(model.CanEditTender); Assert.Equal(0, terminal.Calls); Assert.Equal(0, print.Calls);
        await using var db = Db(); Assert.Empty(await db.CheckoutOperations.ToListAsync());
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Equal(1001, cart.Count);
    }

    [AvaloniaFact]
    public async Task DocumentWorkload_DraftAndCartLimitsAllowRemovalAndExistingQuantityUpdates()
    {
        var cart = new CartStateStore();
        for (int i = 0; i < CommercialDocumentLimits.Lines; i++) cart.Items.Add(new CartItemDto("P" + i, "Producto", 1m, 1));
        Assert.Throws<InvalidOperationException>(() => cart.AddItem(new("NEW", "Nuevo", 1m, 1), 10));
        cart.AddItem(new("P0", "Producto", 1m, 1), 10);
        Assert.Equal(2, cart.Items[0].Quantity);
        cart.RemoveItem(cart.Items[^1]); cart.AddItem(new("NEW", "Nuevo", 1m, 1), 10);
        Assert.Equal(1000, cart.Items.Count);
        var model = new PurchaseOrderViewModel(new PurchaseService(new TestDbContextFactory(_options), _actor), ProductsService());
        model.SelectedProduct = await ProductsService().GetByBarcodeAsync("BARCODE");
        model.PurchaseUnitPrice = "4"; model.PurchaseQuantity = "1";
        for (int i = 0; i < 1000; i++) model.DraftPurchaseItems.Add(new("L" + i, "", "P", "Producto", 4m, 1, 4m));
        model.AddItemToDraftCommand.Execute(null);
        Assert.True(model.IsFeedbackError); Assert.Equal(1000, model.DraftPurchaseItems.Count);
        model.RemoveItemFromDraftCommand.Execute(model.DraftPurchaseItems[^1]); model.AddItemToDraftCommand.Execute(null);
        Assert.Equal(1000, model.DraftPurchaseItems.Count); Assert.Empty(model.FeedbackMessage); Assert.False(model.IsFeedbackError);
    }

    private sealed class DocumentMaterializationProbe : IMaterializationInterceptor
    {
        public int Count;
        public int Sales;
        public bool Oversize;
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is SaleEntity) Sales++;
            if (entity is CheckoutOperationEntity checkout)
            {
                Count++;
                Oversize |= Encoding.UTF8.GetByteCount(checkout.SalePayload) > CommercialDocumentLimits.PayloadBytes ||
                    checkout.FiscalPayload is not null && Encoding.UTF8.GetByteCount(checkout.FiscalPayload) > CommercialDocumentLimits.PayloadBytes;
            }
            return entity;
        }
    }
}
