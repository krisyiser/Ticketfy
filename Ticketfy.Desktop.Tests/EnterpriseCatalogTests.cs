using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.State;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task Inventory_MinimumIsPersistedAuditedAndPhysicalValuationDoesNotCountKitsTwice()
    {
        await using var db = Db();
        db.Products.Add(new ProductEntity { Id = "LOW", Barcode = "LOW", Name = "Low", Price = 5m, Cost = 2m, Stock = 4 });
        db.Products.Add(new ProductEntity { Id = "VIRTUAL", Barcode = "VIRTUAL", Name = "Virtual kit", IsKit = true, Price = 19m });
        db.ItemKits.Add(new ItemKitEntity { Id = "VIRTUAL", ParentProductId = "VIRTUAL", KitBarcode = "VIRTUAL", Name = "Virtual kit", Price = 19m });
        db.ItemKitItems.Add(new ItemKitItemEntity { ItemKitId = "VIRTUAL", ProductId = "P", Quantity = 2 });
        await db.SaveChangesAsync();
        var products = ProductsService();
        var catalog = new Ticketfy.ViewModels.Inventory.InventoryCatalogViewModel(products);
        await catalog.LoadProductsAsync();
        Assert.Equal(68m, catalog.TotalStockCost);
        Assert.Equal(220m, catalog.TotalStockSale);
        catalog.ShowOnlyLowStock = true;
        await catalog.LoadProductsAsync();
        Assert.Equal("LOW", Assert.Single(catalog.FilteredProducts).Id);
        await catalog.SetDefaultMinStockAsync(2);
        Assert.Empty(catalog.FilteredProducts);
        Assert.Equal(2, await products.GetDefaultMinStockAsync());
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "INVENTORY_MINIMUM_CHANGED").ToListAsync());
        (await db.Products.SingleAsync(p => p.Id == "LOW")).MinStock = 7;
        await db.SaveChangesAsync();
        await catalog.LoadProductsAsync();
        Assert.Equal("LOW", Assert.Single(catalog.FilteredProducts).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.SetDefaultMinStockAsync(double.NaN));
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => products.SetDefaultMinStockAsync(1));
    }
    private sealed class NoNetworkCatalog : IExternalCatalogService
    {
        public Task<ProductDto?> FetchProductByBarcodeAsync(string barcode) => throw new InvalidOperationException("No network in synthetic catalog tests.");
    }

    [AvaloniaFact]
    public async Task PosCatalog_PagesEntireCatalogScansUnloadedProductAndNeverGuessesPartialNames()
    {
        await using (var db = Db())
        {
            for (int i = 0; i < 125; i++) db.Products.Add(new ProductEntity
            { Id = "CAT-" + i.ToString("D3"), Barcode = "CODE-" + i.ToString("D3"), Name = "Alpha " + i.ToString("D3"), Category = "Synthetic", Stock = 10, Price = 5m });
            await db.SaveChangesAsync();
        }
        var cart = new CartStateStore();
        var model = new CatalogViewModel(ProductsService(), new NoNetworkCatalog(), cart);
        await model.LoadProductsAsync();
        Assert.Equal(60, model.Products.Count);
        Assert.True(model.HasNextPage);
        Assert.Equal(126, model.CategoryChips.Single(c => c.Name == "Todos").Count);
        await model.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(60, model.Products.Count);
        await model.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(6, model.Products.Count);
        Assert.False(model.HasNextPage);
        await model.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(60, model.Products.Count);
        model.SearchQuery = "Alpha";
        await model.ProcessScanOrSearchSubmitCommand.ExecuteAsync(null);
        Assert.Empty(cart.Items);
        Assert.Contains("Seleccione", model.FeedbackMessage);
        model.SearchQuery = "2*CODE-124";
        await model.ProcessScanOrSearchSubmitCommand.ExecuteAsync(null);
        Assert.Equal("CAT-124", Assert.Single(cart.Items).ProductId);
        Assert.Equal(2d, cart.Items.Single().Quantity);
        model.SearchQuery = "0.5*CODE-124";
        await model.ProcessScanOrSearchSubmitCommand.ExecuteAsync(null);
        Assert.True(model.FeedbackIsError);
        Assert.Equal(2d, cart.Items.Single().Quantity);
        model.ClearSessionData();
        await Task.Delay(180); // Any pending search must not repopulate data after session invalidation.
        Assert.Empty(model.Products);
        Assert.Empty(model.FilteredProducts);
    }

    [Fact]
    public async Task ProductPages_SearchAcrossPagesFilterCategoriesAndRejectInvalidBounds()
    {
        var service = ProductsService();
        Assert.Single((await service.GetPageAsync(0, 1, "producto")).Items);
        Assert.Empty((await service.GetPageAsync(0, 60, category: "missing")).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(0, 201));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetPageAsync(int.MaxValue, 60));
        await using var db = Db();
        (await db.Products.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        Assert.Empty(await service.GetCategoryCountsAsync());
        Assert.Empty((await service.GetPageAsync(0)).Items);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(0.0001d)]
    public void Cart_RejectsNonFiniteOrUnrepresentableQuantity(double amount)
    {
        var cart = new CartStateStore();
        Assert.Throws<InvalidOperationException>(() => cart.AddItem(new CartItemDto("P", "Synthetic", 1m, amount), 100d));
        Assert.Empty(cart.Items);
    }
}
