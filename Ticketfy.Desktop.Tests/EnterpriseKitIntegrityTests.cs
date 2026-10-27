using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task SeedGraphKitAsync(string id, params (string Product, double Quantity)[] parts)
    {
        await using var db = Db();
        db.Products.Add(new ProductEntity { Id = id, Barcode = id, Name = id, Price = 10m, IsKit = true });
        var recipe = new ItemKitEntity { Id = id + "-RECIPE", ParentProductId = id, KitBarcode = id, Name = id, Price = 10m };
        foreach (var part in parts)
            recipe.Components.Add(new ItemKitItemEntity { ItemKitId = recipe.Id, ProductId = part.Product, Quantity = part.Quantity });
        db.ItemKits.Add(recipe);
        await db.SaveChangesAsync();
    }

    private static SaleDto GraphKitSale(string operation, string id, double quantity = 1) =>
        Sale(operation, 10m * (decimal)quantity) with
        {
            Items = [new(id, id, 10m, 0m, quantity, "Pza", "General", 0m, 10m * (decimal)quantity)],
            GlobalDiscountAmount = 0
        };

    [Theory]
    [InlineData("duplicate-recipe")]
    [InlineData("foreign-parent")]
    [InlineData("fractional-piece")]
    [InlineData("overwide")]
    [InlineData("excess-precision")]
    public async Task KitGraph_InvalidCompositionIsUnavailableAndCannotPostAnyFinancialRecords(string anomaly)
    {
        await SeedGraphKitAsync("GRAPH", ("P", anomaly == "fractional-piece" ? .5 : anomaly == "excess-precision" ? .0001 : 1));
        await using (var db = Db())
        {
            var recipe = await db.ItemKits.SingleAsync();
            if (anomaly == "duplicate-recipe")
                db.ItemKits.Add(new ItemKitEntity { Id = "DUPLICATE", ParentProductId = "GRAPH", KitBarcode = "DUPLICATE", Name = "Ambiguous", Components = [new() { ProductId = "P", Quantity = 2 }] });
            if (anomaly == "foreign-parent")
            {
                // Old fallback by recipe ID must never borrow another product's composition.
                db.ItemKitItems.RemoveRange(db.ItemKitItems);
                db.ItemKits.Remove(recipe);
                await db.SaveChangesAsync();
                db.ItemKits.Add(new ItemKitEntity { Id = "GRAPH", ParentProductId = "P", KitBarcode = "FOREIGN", Name = "Foreign", Components = [new() { ProductId = "P", Quantity = 1 }] });
            }
            if (anomaly == "overwide")
            {
                (await db.Products.SingleAsync(p => p.Id == "P")).Stock = 1000;
                db.ItemKitItems.AddRange(Enumerable.Range(0, 200).Select(_ => new ItemKitItemEntity
                { ItemKitId = recipe.Id, ProductId = "P", Quantity = 1 }));
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await ProductsService().GetByIdAsync("GRAPH"))!.Stock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(GraphKitSale("BAD-GRAPH", "GRAPH")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(GraphKitSale("BAD-GRAPH", "GRAPH")));
        await using var verify = Db();
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Empty(await verify.SalePayments.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
        Assert.Empty(await verify.CheckoutOperations.ToListAsync());
        Assert.Empty(await verify.TransactionJournal.ToListAsync());
        Assert.Equal(0m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Fact]
    public async Task KitGraph_CheckoutPreflightAccountsForEverySharedIngredientBeforeStartingACheckout()
    {
        await SeedGraphKitAsync("SHARED-A", ("P", 12));
        await SeedGraphKitAsync("SHARED-B", ("P", 12));
        var first = GraphKitSale("EXCESS-SHARED", "SHARED-A");
        var second = GraphKitSale("EXCESS-SHARED", "SHARED-B");
        var sale = first with { Items = [.. first.Items, .. second.Items], Total = 20m, CashAmount = 20m, PaidAmount = 20m };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(sale));
        await using var verify = Db();
        Assert.Empty(await verify.CheckoutOperations.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task KitGraph_OverdeepCompositionCannotBypassTheCatalogAtCheckout()
    {
        for (int i = 33; i >= 0; i--)
            await SeedGraphKitAsync("DEEP-" + i, (i == 33 ? "P" : "DEEP-" + (i + 1), 1));
        Assert.Equal(0, (await ProductsService().GetByIdAsync("DEEP-0"))!.Stock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(GraphKitSale("DEEP-SALE", "DEEP-0")));
        await using var verify = Db();
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Equal(20, (await verify.Products.SingleAsync(p => p.Id == "P")).Stock);
    }

    [Fact]
    public async Task KitGraph_SharedIngredientsAreConsolidatedInDecimalAndReturnsUseTheOriginalMovement()
    {
        await SeedGraphKitAsync("CHILD", ("P", 2));
        await SeedGraphKitAsync("PARENT", ("CHILD", 1), ("P", 3));
        Assert.Equal(4, (await ProductsService().GetByIdAsync("PARENT"))!.Stock);
        var sale = await Sales.SaveAsync(GraphKitSale("SHARED-LEAVES", "PARENT", 2));
        await Sales.SaveAsync(GraphKitSale("SHARED-LEAVES", "PARENT", 2));
        await using (var db = Db())
        {
            var movement = Assert.Single(await db.InventoryMovements.ToListAsync());
            Assert.Equal(-10m, movement.Quantity);
            Assert.Equal("P", movement.ProductId);
            Assert.Equal(30m, (await db.SaleLines.SingleAsync()).CostTotal);
            // Later recipe changes must never change what a return restores.
            (await db.ItemKitItems.FirstAsync(i => i.ItemKitId == "CHILD-RECIPE")).Quantity = 4;
            await db.SaveChangesAsync();
        }
        await Sales.ProcessPartialReturnAsync(sale.Id, "PARENT", 1, "synthetic return", operationId: "SHARED-RETURN");
        await Sales.ProcessPartialReturnAsync(sale.Id, "PARENT", 1, "synthetic return", operationId: "SHARED-RETURN");
        await using var verify = Db();
        Assert.Equal(15, (await verify.Products.SingleAsync(p => p.Id == "P")).Stock);
        Assert.Equal(15m, (await verify.ReturnLines.SingleAsync()).CostReversed);
        Assert.Equal(5m, (await verify.InventoryMovements.SingleAsync(m => m.Reason != "SALE")).Quantity);
    }

    [Fact]
    public async Task KitGraph_GenericProductEditorCannotInventOrConvertAnInventoryKind()
    {
        var products = ProductsService();
        var physical = (await products.GetByIdAsync("P"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.UpdateAsync(physical with { IsKit = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.AddAsync(physical with { Id = "UNDEFINED", Barcode = "UNDEFINED", IsKit = true, Stock = 0 }));
        await SeedGraphKitAsync("VIRTUAL-KIND", ("P", 1));
        var kit = (await products.GetByIdAsync("VIRTUAL-KIND"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.UpdateAsync(kit with { IsKit = false, StockAdjustmentReason = "not a conversion workflow" }));
        await using var verify = Db();
        Assert.False((await verify.Products.SingleAsync(p => p.Id == "P")).IsKit);
        Assert.True((await verify.Products.SingleAsync(p => p.Id == "VIRTUAL-KIND")).IsKit);
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task KitGraph_WriterUsesTheSharedValidatorAndRollsBackItsProvisionalCatalogAndJournal()
    {
        await SeedGraphKitAsync("INVALID-CHILD", ("P", .5));
        var service = new ItemKitService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<Ticketfy.Services.KitCommandRejectedException>(() => service.SaveAsync(
            new("REJECTED-ROOT", "REJECTED-ROOT", "Rejected", 10m, "", [new("", "", "INVALID-CHILD", "Invalid child", 1)]), "REJECTED-ROOT"));
        await using var verify = Db();
        Assert.False(await verify.Products.AnyAsync(p => p.Id == "REJECTED-ROOT"));
        Assert.False(await verify.ItemKits.AnyAsync(p => p.ParentProductId == "REJECTED-ROOT"));
        Assert.Single(await verify.ItemKitItems.ToListAsync());
        Assert.Empty(await verify.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task KitGraph_StaleRecipeBarcodeCannotReparentAnUnrelatedDefinitionWhenCreatingAProduct()
    {
        await SeedGraphKitAsync("EXISTING", ("P", 2));
        await using (var db = Db())
        {
            (await db.ItemKits.SingleAsync()).KitBarcode = "NEW-CODE";
            await db.SaveChangesAsync();
        }
        var service = new ItemKitService(new TestDbContextFactory(_options), _actor);
        await service.SaveAsync(new("NEW-KIT", "NEW-CODE", "New kit", 10m, "",
            [new("", "", "P", "Physical", 1)]), "CREATE");
        await using var verify = Db();
        Assert.Equal("EXISTING", (await verify.ItemKits.SingleAsync(k => k.Id == "EXISTING-RECIPE")).ParentProductId);
        Assert.Equal(2, (await verify.ItemKitItems.SingleAsync(i => i.ItemKitId == "EXISTING-RECIPE")).Quantity);
        Assert.Equal(2, await verify.ItemKits.CountAsync());
    }

    [Theory]
    [InlineData("missing-product")]
    [InlineData("null-item")]
    [InlineData("null-list")]
    public async Task KitGraph_WriterRejectsMalformedInputInsteadOfSilentlyDroppingComponents(string invalid)
    {
        var service = new ItemKitService(new TestDbContextFactory(_options), _actor);
        List<ItemKitItemDto> items = [new("", "", "P", "Physical", 1)];
        if (invalid == "missing-product") items.Add(new("", "", " ", "Invalid", 1));
        if (invalid == "null-item") items.Add(null!);
        if (invalid == "null-list") items = null!;
        await Assert.ThrowsAsync<Ticketfy.Services.KitCommandRejectedException>(() => service.SaveAsync(
            new("BAD-INPUT", "BAD-INPUT", "Bad input", 10m, "", items), "BAD-INPUT"));
        await using var verify = Db();
        Assert.Empty(await verify.ItemKits.ToListAsync());
        Assert.Empty(await verify.TransactionJournal.ToListAsync());
    }
}
