using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentBoundary_ExcessSaleLinesCannotCreateAnIntentOrPostMoney(bool direct)
    {
        await using var db = Db();
        (await db.Products.SingleAsync()).Stock = 5000; await db.SaveChangesAsync();
        var source = Sale("TOO-MANY");
        var sale = source with { Items = Enumerable.Repeat(source.Items[0], 1001).ToList(), Total = 20020m, PaidAmount = 20020m, CashAmount = 20020m };
        if (direct) await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(sale));
        Assert.Empty(await db.CheckoutOperations.ToListAsync()); Assert.Empty(await db.Sales.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync()); Assert.Empty(await db.SalePayments.ToListAsync());
        Assert.Equal(5000d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
    }

    [Fact]
    public async Task DocumentBoundary_ExcessPurchaseLinesCannotChangeStockCostOrDebt()
    {
        var purchase = new PurchaseDto("BIG-BUY", "S", "Proveedor", "F-BIG", "", 0m, "",
            Enumerable.Range(0, 1001).Select(i => new PurchaseItemDto("L" + i, "BIG-BUY", "P", "Producto", 4m, 1, 4m)).ToList());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor).RegisterPurchaseAsync(purchase));
        await using var db = Db();
        Assert.Empty(await db.Purchases.ToListAsync()); Assert.Empty(await db.PurchaseItems.ToListAsync());
        Assert.Empty(await db.AccountEntries.ToListAsync()); Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock); Assert.Equal(3m, (await db.Products.SingleAsync()).Cost);
    }

    [Fact]
    public async Task DocumentBoundary_ExcessTextIsRejectedBeforePersistingCheckout()
    {
        var sale = Sale("BIG-TEXT");
        sale = sale with { Items = [sale.Items[0] with { Name = new string('x', 1025) }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(sale));
        await using var db = Db(); Assert.Empty(await db.CheckoutOperations.ToListAsync());
    }

    [Fact]
    public async Task DocumentBoundary_ExcessPurchaseNotesCannotBeSaved()
    {
        var purchase = new PurchaseDto("BIG-NOTES", "S", "Proveedor", "F-BIG", "", 0m, new string('x', 2001),
            [new("L", "BIG-NOTES", "P", "Producto", 4m, 1, 4m)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor).RegisterPurchaseAsync(purchase));
        await using var db = Db(); Assert.Empty(await db.Purchases.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentBoundary_NullLinesAreValidationFailuresNotUnhandledExceptions(bool purchase)
    {
        if (purchase)
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor)
                .RegisterPurchaseAsync(new("NULL-LINE", "S", "Proveedor", "F-NULL", "", 0m, "", [null!])));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("NULL-LINE") with { Items = [null!] }));
    }

    [Fact]
    public async Task DocumentBoundary_OversizedHistoricalCheckoutRemainsUnresolvedAndUnchanged()
    {
        var row = UnfundedStartupIntent("HUGE-LEGACY");
        string original = new string(' ', 2 * 1024 * 1024) + row.SalePayload;
        row.SalePayload = original;
        await using (var db = Db()) { db.CheckoutOperations.Add(row); await db.SaveChangesAsync(); }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db(); var retained = await verify.CheckoutOperations.SingleAsync();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, retained.State);
        Assert.Equal(original, retained.SalePayload);
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Single((await Sales.GetInterruptedCheckoutsPageAsync()).Items);
    }
}
