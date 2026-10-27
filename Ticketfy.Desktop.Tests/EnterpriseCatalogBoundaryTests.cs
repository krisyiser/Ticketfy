using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("products")]
    [InlineData("pos")]
    [InlineData("customers")]
    [InlineData("suppliers")]
    [InlineData("purchases")]
    public async Task LegacyCatalogReads_RejectOversizeInsteadOfAllocatingOrSilentlyTruncating(string catalog)
    {
        await SeedCatalogBoundaryAsync(200);
        var factory = new TestDbContextFactory(_options);
        async Task ReadAsync()
        {
            switch (catalog)
            {
                case "products": await ProductsService().GetAllAsync(); break;
                case "pos": await ProductsService().GetCatalogForPosAsync(); break;
                case "customers": await CustomersService().GetAllAsync(); break;
                case "suppliers": await new SupplierService(factory, _actor).GetAllAsync(); break;
                case "purchases": await new PurchaseService(factory, _actor).GetAllAsync(); break;
            }
        }
        // Each catalog has exactly 200 active documents: compatibility remains explicit and complete.
        await ReadAsync();
        await SeedCatalogBoundaryAsync(201, first: 200);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(ReadAsync);
        Assert.Contains("200", error.Message);
        Assert.Contains("paginada", error.Message);
    }

    [Fact]
    public async Task CatalogPages_ReturnEveryMatchingDocumentBeyondLegacyLimitAndNeverFallbackToFullReads()
    {
        await SeedCatalogBoundaryAsync(255);
        var factory = new TestDbContextFactory(_options);
        var products = ProductsService();
        var customers = CustomersService();
        var suppliers = new SupplierService(factory, _actor);
        var purchases = new PurchaseService(factory, _actor);
        var productIds = new List<string>();
        var customerIds = new List<string>();
        var supplierIds = new List<string>();
        var purchaseIds = new List<string>();
        for (int page = 0; page < 3; page++)
        {
            var pp = await products.GetPageAsync(page, 100);
            var cp = await customers.GetPageAsync(page, 100);
            var sp = await suppliers.GetPageAsync(page, 100);
            var bp = await purchases.GetPageAsync(page, 100);
            Assert.Equal(page < 2, pp.HasMore);
            Assert.Equal(page < 2, cp.HasMore);
            Assert.Equal(page < 2, sp.HasMore);
            productIds.AddRange(pp.Items.Select(p => p.Id));
            customerIds.AddRange(cp.Items.Select(c => c.Id));
            supplierIds.AddRange(sp.Items.Select(s => s.Id));
            purchaseIds.AddRange(bp.Select(p => p.Id));
            Assert.All(bp, p =>
            {
                var line = Assert.Single(p.Items);
                Assert.Equal(p.Id, line.PurchaseId);
                Assert.Equal(p.TotalCost, line.TotalPrice);
            });
        }
        foreach (var ids in new[] { productIds, customerIds, supplierIds, purchaseIds })
        { Assert.Equal(255, ids.Count); Assert.Equal(255, ids.Distinct().Count()); }
        Assert.Empty((await products.GetPageAsync(3, 100)).Items);
        Assert.Empty(await purchases.GetPageAsync(3, 100));
        Assert.Equal("P-0254", Assert.Single((await products.GetPageAsync(0, search: "0254")).Items).Id);
        Assert.Equal("C-0254", Assert.Single(await customers.SearchAsync("0254")).Id);
        Assert.Equal("S-0254", Assert.Single(await suppliers.SearchAsync("0254")).Id);
        Assert.Equal("buy-0254", Assert.Single(await purchases.GetPageAsync(0, search: "buy-0254")).Id);
        Assert.Equal(100, (await customers.SearchAsync()).Count);
        Assert.Equal(100, (await suppliers.SearchAsync()).Count);
        Assert.Equal(255, (await products.GetCategoryCountsAsync()).Sum(c => c.Count));
    }

    [Fact]
    public async Task LegacyProductCategory_PreservesExactEmptyCategoryAndRejectsOversizedCategory()
    {
        await SeedCatalogBoundaryAsync(201);
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "P")).Category = "";
            await db.SaveChangesAsync();
        }
        Assert.Equal("P", Assert.Single(await ProductsService().GetByCategoryAsync("")).Id);
        Assert.Equal(200, (await ProductsService().GetByCategoryAsync("General")).Count);
        await SeedCatalogBoundaryAsync(202, first: 201);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductsService().GetByCategoryAsync("General"));
        Assert.Empty(await ProductsService().GetByCategoryAsync("No existe"));
    }

    private async Task SeedCatalogBoundaryAsync(int count, int first = 1)
    {
        await using var db = Db();
        if (first == 1)
        {
            db.Purchases.Add(new PurchaseEntity { Id = "buy-0000", SupplierId = "S", SupplierName = "Proveedor", TotalCost = 3m });
            db.PurchaseItems.Add(new PurchaseItemEntity { Id = "L-0000", PurchaseId = "buy-0000", ProductId = "P", Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
        }
        for (int i = first; i < count; i++)
        {
            db.Products.Add(new ProductEntity { Id = $"P-{i:D4}", Barcode = $"BC-{i:D4}", Name = $"Producto {i:D4}", Category = "General", Stock = 1, Price = 5m, Cost = 3m });
            db.Customers.Add(new CustomerEntity { Id = $"C-{i:D4}", Name = $"Cliente {i:D4}", CustomerCode = $"CLI-{i:D4}" });
            db.Suppliers.Add(new SupplierEntity { Id = $"S-{i:D4}", Name = $"Proveedor {i:D4}", IsActive = 1 });
            db.Purchases.Add(new PurchaseEntity { Id = $"buy-{i:D4}", SupplierId = "S", SupplierName = "Proveedor", TotalCost = 3m });
            db.PurchaseItems.Add(new PurchaseItemEntity { Id = $"L-{i:D4}", PurchaseId = $"buy-{i:D4}", ProductId = "P", Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LegacyCatalogBoundary_IsEnforcedInSqlBeforeLoadingCompleteTablesOrPurchaseLines()
    {
        await SeedCatalogBoundaryAsync(201);
        var observer = new CatalogLimitObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var factory = new TestDbContextFactory(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProductService(factory, _actor).GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerService(factory, _actor).GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SupplierService(factory, _actor).GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(factory, _actor).GetAllAsync());
        Assert.Equal(4, observer.CatalogQueries);
        Assert.Equal(0, observer.PurchaseLineQueries);
    }

    private sealed class CatalogLimitObserver : DbCommandInterceptor
    {
        public int CatalogQueries { get; private set; }
        public int PurchaseLineQueries { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"PurchaseItems\"", StringComparison.Ordinal)) PurchaseLineQueries++;
            if (new[] { "products", "customers", "Suppliers", "Purchases" }
                .Any(table => command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)))
            {
                CatalogQueries++;
                Assert.Contains("LIMIT", command.CommandText);
                Assert.Contains(command.Parameters.Cast<DbParameter>(), p => int.TryParse(p.Value?.ToString(), out int value) && value == 201);
            }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class EnterpriseCatalogSnapshotTests
{
    [Theory]
    [InlineData("all")]
    [InlineData("category")]
    [InlineData("barcode")]
    [InlineData("id")]
    public async Task ProductLookup_KeepsPriceAndKitAvailabilityInSameSnapshotDuringConcurrentUpdate(string lookup)
    {
        await WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var setup = new AppDbContext(options))
            {
                setup.Products.Add(new ProductEntity { Id = "P", Barcode = "COMPONENT", Name = "Componente", Stock = 10 });
                setup.Products.Add(new ProductEntity { Id = "K", Barcode = "KIT", Name = "Kit", Price = 20m, IsKit = true, Category = "Kits" });
                setup.ItemKits.Add(new ItemKitEntity { Id = "K", ParentProductId = "K", KitBarcode = "KIT", Name = "Kit", Price = 20m });
                setup.ItemKitItems.Add(new ItemKitItemEntity { ItemKitId = "K", ProductId = "P", Quantity = 2 });
                await setup.SaveChangesAsync();
            }
            var interleave = new CatalogReadObserver(async () =>
            {
                await using var writer = new AppDbContext(options);
                (await writer.Products.SingleAsync(p => p.Id == "P")).Stock = 2;
                (await writer.Products.SingleAsync(p => p.Id == "K")).Price = 30m;
                await writer.SaveChangesAsync();
            }, "products");
            var readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(interleave).Options;
            var products = new ProductService(new TestDbContextFactory(readOptions), actor);
            var kit = lookup switch
            {
                "all" => (await products.GetAllAsync()).Single(p => p.Id == "K"),
                "category" => Assert.Single(await products.GetByCategoryAsync("Kits")),
                "barcode" => (await products.GetByBarcodeAsync("KIT"))!,
                _ => (await products.GetByIdAsync("K"))!
            };
            Assert.True(interleave.Committed);
            Assert.Equal(20m, kit.Price);
            Assert.Equal(5d, kit.Stock);
            var current = (await new ProductService(new TestDbContextFactory(options), actor).GetByIdAsync("K"))!;
            Assert.Equal(30m, current.Price);
            Assert.Equal(1d, current.Stock);
        });
    }

    [Theory]
    [InlineData("all")]
    [InlineData("id")]
    [InlineData("page")]
    public async Task PurchaseRead_UsesOneReadTransactionForHeadersAndMatchingLines(string lookup)
    {
        await WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var setup = new AppDbContext(options))
            {
                setup.Purchases.Add(new PurchaseEntity { Id = "BUY", SupplierId = "S", TotalCost = 3m });
                setup.PurchaseItems.Add(new PurchaseItemEntity { Id = "L", PurchaseId = "BUY", ProductId = "P", Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
                await setup.SaveChangesAsync();
            }
            var observer = new CatalogReadObserver();
            var readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options;
            var purchases = new PurchaseService(new TestDbContextFactory(readOptions), actor);
            var purchase = lookup switch
            {
                "all" => Assert.Single(await purchases.GetAllAsync()),
                "page" => Assert.Single(await purchases.GetPageAsync(0)),
                _ => (await purchases.GetByIdAsync("BUY"))!
            };
            Assert.Equal(purchase.TotalCost, Assert.Single(purchase.Items).TotalPrice);
            // Header, size/count preflight and accepted detail must share the same snapshot.
            Assert.Equal(3, observer.PurchaseTransactions.Count);
            Assert.NotNull(observer.PurchaseTransactions[0]);
            Assert.All(observer.PurchaseTransactions, t => Assert.Same(observer.PurchaseTransactions[0], t));
        });
    }

    private sealed class CatalogReadObserver(Func<Task>? write = null, string? table = null) : DbCommandInterceptor
    {
        private int _triggered;
        public bool Committed { get; private set; }
        public List<DbTransaction?> PurchaseTransactions { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Purchases\"", StringComparison.Ordinal)
                || command.CommandText.Contains("FROM \"PurchaseItems\"", StringComparison.Ordinal))
                PurchaseTransactions.Add(command.Transaction);
            if (write is not null && command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _triggered, 1) == 0)
            { await write(); Committed = true; }
            return result;
        }
    }

    internal static async Task WithSyntheticDatabase(Func<DbContextOptions<AppDbContext>, string, CommercialOperationContext, Task> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-catalog-snapshot-{Guid.NewGuid():N}.db");
        string connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            CommercialOperationContext actor;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(setup);
            }
            await test(options, connection, actor);
        }
        finally
        {
            string absolute = Path.GetFullPath(path);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("ticketfy-catalog-snapshot-", Path.GetFileName(absolute), StringComparison.Ordinal);
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(absolute + suffix)) File.Delete(absolute + suffix);
        }
    }
}
