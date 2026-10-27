using System.Data.Common;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.State;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views.Pos;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("cycle")]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("non-finite")]
    [InlineData("negative-cost")]
    [InlineData("excess-cost")]
    [InlineData("bad-stock")]
    public async Task KitGraph_LegacyAnomaliesStayVisibleWithAnExplicitWarning(string anomaly)
    {
        await SeedGraphKitAsync("LEGACY", ("P", 2));
        await using (var db = Db())
        {
            var part = await db.ItemKitItems.SingleAsync();
            var physical = await db.Products.SingleAsync(p => p.Id == "P");
            switch (anomaly)
            {
                case "empty": db.ItemKitItems.Remove(part); break;
                case "cycle": part.ProductId = "LEGACY"; break;
                case "inactive": physical.IsActive = false; break;
                case "negative": part.Quantity = -1; break;
                case "zero": part.Quantity = 0; break;
                case "non-finite": part.Quantity = double.PositiveInfinity; break;
                case "negative-cost": physical.Cost = -1m; break;
                case "excess-cost": physical.Cost = long.MaxValue / 100m; break;
                case "bad-stock": physical.Stock = .5; break;
                case "missing":
                    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
                    try { await db.Database.ExecuteSqlRawAsync("UPDATE item_kit_items SET product_id='MISSING';"); }
                    finally { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;"); }
                    break;
            }
            await db.SaveChangesAsync();
        }
        var dto = (await ProductsService().GetByIdAsync("LEGACY"))!;
        Assert.Equal(0, dto.Stock);
        Assert.StartsWith("Combo no disponible:", dto.InventoryWarning);
        Assert.Equal("REVISAR COMBO", dto.UnavailableLabel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(GraphKitSale("LEGACY-GRAPH", "LEGACY")));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task KitGraph_DepthBoundaryIsTheSameWhenSharedSubtreesHaveAlreadyBeenRead(int depth, bool valid)
    {
        for (int i = depth - 1; i >= 0; i--)
            await SeedGraphKitAsync("BOUNDARY-" + i, (i == depth - 1 ? "P" : "BOUNDARY-" + (i + 1), 1));
        await using var db = Db();
        var graph = await KitInventoryGraph.LoadAsync(db, await db.Products.ToListAsync());
        Assert.Null(graph.Availability("BOUNDARY-" + (depth - 1)).Warning);
        var result = graph.Availability("BOUNDARY-0");
        Assert.Equal(valid ? 20d : 0d, result.Stock);
        Assert.Equal(valid, result.Warning is null);
    }

    [Fact]
    public async Task KitGraph_DecimalCompositionAndProportionalReturnKeepExactThousandths()
    {
        await using (var db = Db())
        {
            var product = await db.Products.SingleAsync(p => p.Id == "P");
            product.IsBulk = true; product.Stock = 1; product.Cost = 1m;
            await db.SaveChangesAsync();
        }
        await SeedGraphKitAsync("TENTH", ("P", .1));
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "TENTH")).IsBulk = true;
            await db.SaveChangesAsync();
        }
        await SeedGraphKitAsync("THREE-HUNDREDTHS", ("TENTH", .3));
        Assert.Equal(33, (await ProductsService().GetByIdAsync("THREE-HUNDREDTHS"))!.Stock);
        var sale = await Sales.SaveAsync(GraphKitSale("EXACT-DECIMAL", "THREE-HUNDREDTHS", 3));
        await using (var db = Db())
        {
            Assert.Equal(-.09m, (await db.InventoryMovements.SingleAsync()).Quantity);
            Assert.Equal(.09m, (await db.SaleLines.SingleAsync()).CostTotal);
        }
        await Sales.ProcessPartialReturnAsync(sale.Id, "THREE-HUNDREDTHS", 1, "synthetic", operationId: "EXACT-RETURN");
        await using var verify = Db();
        Assert.Equal(.94, (await verify.Products.SingleAsync(p => p.Id == "P")).Stock, 6);
        Assert.Equal(.03m, (await verify.ReturnLines.SingleAsync()).CostReversed);
    }

    [Theory]
    [InlineData(.001)]
    [InlineData(1000000000000000d)]
    public async Task KitGraph_UnrepresentableExpansionIsRejectedWithoutRoundingStock(double factor)
    {
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "P")).IsBulk = true;
            await db.SaveChangesAsync();
        }
        await SeedGraphKitAsync("FACTOR", ("P", factor));
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "FACTOR")).IsBulk = true;
            await db.SaveChangesAsync();
        }
        await SeedGraphKitAsync("EXPANSION", ("FACTOR", factor));
        var product = (await ProductsService().GetByIdAsync("EXPANSION"))!;
        Assert.Equal(0, product.Stock);
        Assert.NotNull(product.InventoryWarning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(GraphKitSale("UNREPRESENTABLE", "EXPANSION")));
    }

    [Fact]
    public async Task KitGraph_SharedDagIsMemoizedAndCreatesOnePhysicalMovementPerSaleLine()
    {
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "P")).Stock = 1048576;
            await db.SaveChangesAsync();
        }
        for (int i = 19; i >= 0; i--)
        {
            string child = i == 19 ? "P" : "DAG-" + (i + 1);
            await SeedGraphKitAsync("DAG-" + i, (child, 1), (child, 1));
        }
        Assert.Equal(1, (await ProductsService().GetByIdAsync("DAG-0"))!.Stock);
        await Sales.SaveAsync(GraphKitSale("DAG-SALE", "DAG-0"));
        await using var verify = Db();
        Assert.Equal(-1048576m, (await verify.InventoryMovements.SingleAsync()).Quantity);
        Assert.Equal(3145728m, (await verify.SaleLines.SingleAsync()).CostTotal);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("edges")]
    [InlineData("headers")]
    public async Task KitGraph_VolumeBudgetsBoundQueriesAndNeverReturnAPartialSellableComposition(string budget)
    {
        await SeedGraphKitAsync("WIDE", ("P", 1));
        await using (var db = Db())
        {
            if (budget == "headers")
            {
                db.ItemKits.AddRange(Enumerable.Range(0, KitInventoryGraph.MaximumProducts).Select(i => new ItemKitEntity
                { Id = "DUP-" + i, ParentProductId = "WIDE", KitBarcode = "DUP-" + i, Name = "Duplicate" }));
            }
            else
            {
                int children = budget == "nodes" ? 22 : 42;
                db.ItemKitItems.RemoveRange(db.ItemKitItems);
                for (int i = 0; i < children; i++)
                {
                    string child = "CHILD-" + i;
                    db.Products.Add(new ProductEntity { Id = child, Barcode = child, Name = child, IsKit = true });
                    var recipe = new ItemKitEntity { Id = child, ParentProductId = child, KitBarcode = child, Name = child };
                    db.ItemKits.Add(recipe);
                    db.ItemKitItems.Add(new ItemKitItemEntity { ItemKitId = "WIDE-RECIPE", ProductId = child, Quantity = 1 });
                    for (int j = 0; j < 200; j++)
                    {
                        string ingredient = budget == "nodes" ? $"LEAF-{i}-{j}" : "P";
                        if (budget == "nodes")
                            db.Products.Add(new ProductEntity { Id = ingredient, Barcode = ingredient, Name = ingredient, Stock = 1 });
                        recipe.Components.Add(new ItemKitItemEntity { ItemKitId = child, ProductId = ingredient, Quantity = 1 });
                    }
                }
            }
            await db.SaveChangesAsync();
        }
        var observer = new KitGraphQueryObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new ProductService(new TestDbContextFactory(options), _actor);
        var product = (await service.GetByIdAsync("WIDE"))!;
        Assert.Equal(0, product.Stock);
        Assert.Contains("límite", product.InventoryWarning);
        Assert.NotEmpty(observer.Queries);
        Assert.All(observer.Queries, sql => Assert.Contains("LIMIT", sql));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(GraphKitSale("WIDE-SALE", "WIDE")));
        await using var verify = Db();
        Assert.Empty(await verify.CheckoutOperations.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task KitGraph_TooManyRootsAndOverflowingSaleQuantitiesFailExplicitly()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => KitInventoryGraph.LoadAsync(db,
            Enumerable.Range(0, KitInventoryGraph.MaximumProducts + 1).Select(i => new ProductEntity { Id = i.ToString() }).ToList()));
        await SeedGraphKitAsync("HUGE", ("P", 1000000000000000d));
        var graph = await KitInventoryGraph.LoadAsync(db, await db.Products.ToListAsync());
        var plan = graph.RequirePlan("HUGE");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(GraphKitSale("HUGE-QUANTITY", "HUGE", 1000000000000000d)));
        Assert.Throws<InvalidOperationException>(() => graph.QuantitiesFor(plan, decimal.MaxValue));
    }

    [AvaloniaFact]
    public async Task KitGraph_PosShowsReviewInsteadOfOutOfStockAndExplainsTheInvalidRecipeOnScan()
    {
        await SeedGraphKitAsync("UI-BROKEN", ("P", .5));
        var cart = new CartStateStore();
        var model = new CatalogViewModel(ProductsService(), new NoNetworkCatalog(), cart);
        var view = new PosProductCatalogView();
        var window = Show(view, model);
        try
        {
            await model.LoadProductsAsync();
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "REVISAR COMBO");
            model.SearchQuery = "UI-BROKEN";
            await model.ProcessScanOrSearchSubmitCommand.ExecuteAsync(null);
            Assert.True(model.FeedbackIsError);
            Assert.StartsWith("Combo no disponible:", model.FeedbackMessage);
            Assert.Empty(cart.Items);
        }
        finally { window.Close(); model.ClearSessionData(); }
    }

    private sealed class KitGraphQueryObserver : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"item_kits\"") || command.CommandText.Contains("FROM \"item_kit_items\""))
            {
                Assert.NotNull(command.Transaction);
                Queries.Add(command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }
}
