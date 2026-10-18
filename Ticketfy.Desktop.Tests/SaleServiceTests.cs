using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Xunit;

namespace Ticketfy.Desktop.Tests;

// ── Test Infrastructure Helpers ─────────────────────────────────────────────

/// <summary>
/// Creates a new AppDbContext instance bound to the shared SQLite in-memory connection.
/// Each call returns a fresh DbContext that shares the underlying in-memory database.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new AppDbContext(_options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken _ = default) => Task.FromResult(new AppDbContext(_options));
}

/// <summary>
/// In-memory spy implementation of IAuditService for test assertions.
/// Captures audit logs without a real database.
/// </summary>
internal sealed class SpyAuditService : IAuditService
{
    public List<AuditLogEntity> CapturedLogs { get; } = new();

    public Task LogAsync(AuditLogEntity log)
    {
        if (string.IsNullOrEmpty(log.Id)) log.Id = Guid.NewGuid().ToString();
        CapturedLogs.Add(log);
        return Task.CompletedTask;
    }

    public Task LogAsync(string level, string message, string? meta = null)
    {
        CapturedLogs.Add(new AuditLogEntity { Reason = message, EntityName = "System" });
        return Task.CompletedTask;
    }

    public Task<List<AuditLogEntity>> GetRecentLogsAsync(int limit = 100) =>
        Task.FromResult(CapturedLogs.Take(limit).ToList());

    public Task<bool> VerifyIntegrityAsync() => Task.FromResult(true);
}

// ── SaleService Tests ────────────────────────────────────────────────────────

public sealed class SaleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly SaleService _saleService;
    private readonly SpyAuditService _auditSpy;

    private AppDbContext CreateContext() => new AppDbContext(_options);
    private ProductService ProductService => new ProductService(CreateContext());
    private CustomerService CustomerService => new CustomerService(CreateContext());

    public SaleServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var initCtx = CreateContext())
        {
            initCtx.Database.EnsureCreated();
        }

        _auditSpy = new SpyAuditService();
        _factory = new TestDbContextFactory(_options);

        _saleService = new SaleService(_factory, new CoOccurrenceQueue(), _auditSpy);
    }

    [Fact]
    public async Task SaveAsync_ShouldDeductStockAndRecordSale()
    {
        // 1. Setup Product with stock = 20
        var product = new ProductDto("PROD-101", "12345", "Chips", 5.0, 10.0, 8.0, 5, 20, "Botanas", "Pza", 0, null);
        await ProductService.AddAsync(product);

        // 2. Execute Sale of 3 items
        var itemSnapshot = new SaleItemSnapshotDto("PROD-101", "Chips", 10.0, 5.0, 3, "Pza", "Botanas", 0.0, 30.0);
        var saleDto = new SaleDto(
            Id: "SALE-101",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 30.0,
            TotalCost: 15.0,
            Profit: 15.0,
            PaidAmount: 50.0,
            ChangeAmount: 20.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var savedSale = await _saleService.SaveAsync(saleDto);

        // 3. Verify Sale saved and Stock deducted (20 - 3 = 17)
        Assert.NotNull(savedSale);
        var updatedProduct = await ProductService.GetByIdAsync("PROD-101");
        Assert.NotNull(updatedProduct);
        Assert.Equal(17.0, updatedProduct.Stock);
    }

    [Fact]
    public async Task CreditSale_ShouldIncreaseCustomerDebt()
    {
        // Setup Customer with debt = 0
        var customer = new CustomerDto("CUST-101", "Ana López", "5550001111", "ana@ana.com", "RFC123", 1000.0, 0.0);
        await CustomerService.AddAsync(customer);

        var product = new ProductDto("PROD-102", "999", "Milk", 15.0, 25.0, 0, 0, 10, "Lácteos", "Pza", 0, null);
        await ProductService.AddAsync(product);

        // Execute Credit Sale of total 50.0
        var itemSnapshot = new SaleItemSnapshotDto("PROD-102", "Milk", 25.0, 15.0, 2, "Pza", "Lácteos", 0.0, 50.0);
        var creditSale = new SaleDto(
            Id: "SALE-CREDIT-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 50.0,
            TotalCost: 30.0,
            Profit: 20.0,
            PaidAmount: 0.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Credit",
            CustomerId: "CUST-101",
            IsCredit: true,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        await _saleService.SaveAsync(creditSale);

        // Verify Customer Debt increased to 50.0
        var updatedCustomer = await CustomerService.GetByIdAsync("CUST-101");
        Assert.NotNull(updatedCustomer);
        Assert.Equal(50m, updatedCustomer.Debt);
    }

    [Fact]
    public async Task CancelSale_ShouldRestoreStockAndReduceCreditDebt()
    {
        // Setup Customer debt = 100, Product stock = 5
        var customer = new CustomerDto("CUST-102", "Pedro", "5552223333", "pedro@pedro.com", "RFC123", 1000.0, 0.0);
        await CustomerService.AddAsync(customer);

        var product = new ProductDto("PROD-103", "888", "Juice", 10.0, 20.0, 0, 0, 10, "Bebidas", "Pza", 0, null);
        await ProductService.AddAsync(product);

        var itemSnapshot = new SaleItemSnapshotDto("PROD-103", "Juice", 20.0, 10.0, 5, "Pza", "Bebidas", 0.0, 100.0);
        var saleDto = new SaleDto(
            Id: "SALE-CANCEL-TEST",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 100.0,
            TotalCost: 50.0,
            Profit: 50.0,
            PaidAmount: 0.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Credit",
            CustomerId: "CUST-102",
            IsCredit: true,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var savedSale = await _saleService.SaveAsync(saleDto);

        // Verify Stock is 5 and Debt is 100 before cancellation
        var prodBeforeCancel = await ProductService.GetByIdAsync("PROD-103");
        Assert.Equal(5.0, prodBeforeCancel!.Stock);

        // Cancel Sale using savedSale.Id
        await _saleService.CancelAsync(savedSale.Id);

        // Verify Stock restored to 10 and Customer debt reduced back to 0
        var prodAfterCancel = await ProductService.GetByIdAsync("PROD-103");
        var custAfterCancel = await CustomerService.GetByIdAsync("CUST-102");

        Assert.Equal(10.0, prodAfterCancel!.Stock);
        Assert.Equal(0m, custAfterCancel!.Debt);
    }

    [Fact]
    public async Task ProcessPartialReturnAsync_ShouldCascadeKitRestockToIngredients()
    {
        // 1. Setup kit product and ingredients
        using (var ctx = CreateContext())
        {
            var ing1 = new ProductEntity { Id = "ING-01", Barcode = "111", Name = "Ingredient 1", Cost = 2m, Price = 4m, Stock = 10.0, Category = "Ing", Unit = "Pza" };
            var ing2 = new ProductEntity { Id = "ING-02", Barcode = "222", Name = "Ingredient 2", Cost = 3m, Price = 6m, Stock = 10.0, Category = "Ing", Unit = "Pza" };
            var kitProduct = new ProductEntity { Id = "KIT-01", Barcode = "333", Name = "Combo Desayuno", Cost = 5m, Price = 15m, Stock = 0.0, Category = "Combo", Unit = "Pza", IsKit = true };

            ctx.Products.AddRange(ing1, ing2, kitProduct);

            var itemKitEntity = new ItemKitEntity
            {
                Id = "KIT-01-DEF",
                ParentProductId = "KIT-01",
                KitBarcode = "333",
                Name = "Combo Desayuno",
                Price = 15m
            };
            itemKitEntity.Components.Add(new ItemKitItemEntity { Id = "COMP-1", ItemKitId = "KIT-01-DEF", ProductId = "ING-01", Quantity = 2.0 });
            itemKitEntity.Components.Add(new ItemKitItemEntity { Id = "COMP-2", ItemKitId = "KIT-01-DEF", ProductId = "ING-02", Quantity = 1.0 });

            ctx.ItemKits.Add(itemKitEntity);
            await ctx.SaveChangesAsync();
        }

        // 2. Setup original sale
        var itemSnapshot = new SaleItemSnapshotDto("KIT-01", "Combo Desayuno", 15.0, 5.0, 1.0, "Pza", "Combo", 0.0, 15.0);
        var saleDto = new SaleDto(
            Id: "SALE-KIT-RETURN-TEST",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 15.0,
            TotalCost: 7.0,
            Profit: 8.0,
            PaidAmount: 15.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Efectivo",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );
        var savedSale = await _saleService.SaveAsync(saleDto);

        // Verify stock after sale but before return
        var ing1AfterSale = await ProductService.GetByIdAsync("ING-01");
        var ing2AfterSale = await ProductService.GetByIdAsync("ING-02");
        Assert.Equal(8.0, ing1AfterSale!.Stock);
        Assert.Equal(9.0, ing2AfterSale!.Stock);

        // 3. Process return of 1x Combo using savedSale.Id
        var success = await _saleService.ProcessPartialReturnAsync(savedSale.Id, "KIT-01", 1.0, "Devolucion", "Efectivo");
        Assert.True(success);

        // 4. Verify parent SKU is still 0 stock, ingredient 1 has 10 stock, ingredient 2 has 10 stock
        var parent = await ProductService.GetByIdAsync("KIT-01");
        var restoredIng1 = await ProductService.GetByIdAsync("ING-01");
        var restoredIng2 = await ProductService.GetByIdAsync("ING-02");

        Assert.Equal(0.0, parent!.Stock);
        Assert.Equal(10.0, restoredIng1!.Stock);
        Assert.Equal(10.0, restoredIng2!.Stock);
    }

    [Fact]
    public async Task SaveAsync_ShouldProrateGlobalDiscountAndCalculateVAT()
    {
        var prodA = new ProductDto("PROD-A", "1001", "Item A", 50.0, 100.0, 0, 0, 10, "General", "Pza", 0, null);
        var prodB = new ProductDto("PROD-B", "1002", "Item B", 25.0, 50.0, 0, 0, 10, "General", "Pza", 0, null);
        await ProductService.AddAsync(prodA);
        await ProductService.AddAsync(prodB);

        var itemA = new SaleItemSnapshotDto("PROD-A", "Item A", 100.0, 50.0, 1, "Pza", "General", 0.0, 100.0);
        var itemB = new SaleItemSnapshotDto("PROD-B", "Item B", 50.0, 25.0, 1, "Pza", "General", 0.0, 50.0);

        var saleDto = new SaleDto(
            Id: "SALE-PRORATE-TEST",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemA, itemB],
            Total: 120.0,
            TotalCost: 75.0,
            Profit: 45.0,
            PaidAmount: 120.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var savedSale = await _saleService.SaveAsync(saleDto);
        Assert.NotNull(savedSale);

        var savedItems = savedSale.Items;
        Assert.Equal(2, savedItems.Count);

        var savedA = savedItems.First(i => i.ProductId == "PROD-A");
        var savedB = savedItems.First(i => i.ProductId == "PROD-B");

        Assert.Equal(20m, savedA.ProratedGlobalDiscountAmount);
        Assert.Equal(11.03m, savedA.TaxAmount);
        Assert.Equal(80m, savedA.TotalPrice);

        Assert.Equal(10m, savedB.ProratedGlobalDiscountAmount);
        Assert.Equal(5.52m, savedB.TaxAmount);
        Assert.Equal(40m, savedB.TotalPrice);
    }

    [Fact]
    public async Task ProcessPartialReturnAsync_ShouldEnforceAntiFraudQuantityLimit()
    {
        var product = new ProductDto("PROD-X", "9999", "Item X", 10.0, 20.0, 0, 0, 10, "General", "Pza", 0, null);
        await ProductService.AddAsync(product);

        var item = new SaleItemSnapshotDto("PROD-X", "Item X", 20.0, 10.0, 2, "Pza", "General", 0.0, 40.0);
        var saleDto = new SaleDto(
            Id: "SALE-ANTI-FRAUD",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item],
            Total: 40.0,
            TotalCost: 20.0,
            Profit: 20.0,
            PaidAmount: 40.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var savedSale = await _saleService.SaveAsync(saleDto);

        var success = await _saleService.ProcessPartialReturnAsync(savedSale.Id, "PROD-X", 1.0, "Reason");
        Assert.True(success);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _saleService.ProcessPartialReturnAsync(savedSale.Id, "PROD-X", 2.0, "Reason");
        });
    }

    [Fact]
    public async Task RegisterPurchaseAsync_ShouldBlockNonPositiveCosts()
    {
        var purchaseService = new PurchaseService(CreateContext());

        var items = new List<PurchaseItemDto>
        {
            new PurchaseItemDto("ITEM-1", "PURCHASE-1", "PROD-1", "Product 1", 0.0, 10, 0.0)
        };

        var purchaseDto = new PurchaseDto(
            Id: "PURCHASE-1",
            SupplierId: "SUPP-1",
            SupplierName: "Supplier 1",
            InvoiceNumber: "INV-123",
            Date: DateTime.Now.ToString("g"),
            TotalCost: 0m,
            Notes: "Test notes",
            Items: items
        );

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await purchaseService.RegisterPurchaseAsync(purchaseDto);
        });
    }

    [Fact]
    public async Task RegisterPurchaseAsync_ShouldUpdateToMostRecentCost()
    {
        var context = CreateContext();
        var supp = new SupplierEntity
        {
            Id = "SUPP-COCA-COLA",
            Name = "Coca-Cola Mexico"
        };
        context.Suppliers.Add(supp);

        var prod = new ProductEntity
        {
            Id = "PROD-COST-TEST",
            Name = "Test Product",
            Cost = 15m,
            Price = 25m,
            Stock = 10.0
        };
        context.Products.Add(prod);
        await context.SaveChangesAsync();

        var purchaseService = new PurchaseService(context);

        var items = new List<PurchaseItemDto>
        {
            new PurchaseItemDto("ITEM-10", "PURCHASE-10", "PROD-COST-TEST", "Test Product", 18.0, 5, 90.0)
        };

        var purchaseDto = new PurchaseDto(
            Id: "PURCHASE-10",
            SupplierId: "SUPP-COCA-COLA",
            SupplierName: "Coca-Cola Mexico",
            InvoiceNumber: "INV-999",
            Date: DateTime.Now.ToString("g"),
            TotalCost: 90m,
            Notes: "Test cost update",
            Items: items
        );

        var result = await purchaseService.RegisterPurchaseAsync(purchaseDto);

        var updatedProd = await context.Products.FindAsync("PROD-COST-TEST");
        Assert.NotNull(updatedProd);
        Assert.Equal(18m, updatedProd.Cost);
        Assert.Equal(15.0, updatedProd.Stock);
        Assert.Equal("SUPP-COCA-COLA", updatedProd.DefaultSupplierId);
    }

    [Fact]
    public void TestDtoSerialization()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        var item = new SaleItemSnapshotDto(
            ProductId: "PROD-A",
            Name: "Item A",
            UnitPrice: 100.0,
            Cost: 50.0,
            Quantity: 1.0,
            Unit: "Pza",
            Category: "General",
            Discount: 0.0,
            TotalPrice: 92.8,
            OriginalUnitPrice: 100.0,
            AppliedDiscountAmount: 0.0,
            AppliedPromotionId: null,
            ProratedGlobalDiscountAmount: 20.0,
            TaxAmount: 12.8,
            ReturnedQuantity: 0.0
        );

        var json = System.Text.Json.JsonSerializer.Serialize(item, options);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SaleItemSnapshotDto>(json, options);

        Assert.Equal(20m, deserialized!.ProratedGlobalDiscountAmount);
        Assert.Equal(12.8m, deserialized.TaxAmount);
    }

    [Fact]
    public async Task SaveAsync_ShouldDistributeLostPennyDiscountRemainderToLastItem()
    {
        var p1 = new ProductDto("P1", "1", "Item 1", 5.0, 10.0, 0, 0, 10, "General", "Pza", 0, null);
        var p2 = new ProductDto("P2", "2", "Item 2", 5.0, 10.0, 0, 0, 10, "General", "Pza", 0, null);
        var p3 = new ProductDto("P3", "3", "Item 3", 5.0, 10.0, 0, 0, 10, "General", "Pza", 0, null);
        await ProductService.AddAsync(p1);
        await ProductService.AddAsync(p2);
        await ProductService.AddAsync(p3);

        var item1 = new SaleItemSnapshotDto("P1", "Item 1", 10.0, 5.0, 1, "Pza", "General", 0.0, 10.0);
        var item2 = new SaleItemSnapshotDto("P2", "Item 2", 10.0, 5.0, 1, "Pza", "General", 0.0, 10.0);
        var item3 = new SaleItemSnapshotDto("P3", "Item 3", 10.0, 5.0, 1, "Pza", "General", 0.0, 10.0);

        var saleDto = new SaleDto(
            Id: "SALE-LOST-PENNY",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item1, item2, item3],
            Total: 20.0,
            TotalCost: 15.0,
            Profit: 5.0,
            PaidAmount: 20.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var savedSale = await _saleService.SaveAsync(saleDto);
        var items = savedSale.Items;
        Assert.Equal(3, items.Count);

        var saved1 = items.First(i => i.ProductId == "P1");
        var saved2 = items.First(i => i.ProductId == "P2");
        var saved3 = items.First(i => i.ProductId == "P3");

        Assert.Equal(3.33m, saved1.ProratedGlobalDiscountAmount);
        Assert.Equal(3.33m, saved2.ProratedGlobalDiscountAmount);
        Assert.Equal(3.34m, saved3.ProratedGlobalDiscountAmount);

        decimal sumDiscounts = items.Sum(i => i.ProratedGlobalDiscountAmount);
        Assert.Equal(10.00m, sumDiscounts);
    }

    [Fact]
    public async Task ProcessSaleAsync_ShouldDeductNestedKitStockRecursively()
    {
        var apple = new ProductDto("APPLE", "111", "Apple", 2.0, 5.0, 0, 0, 10, "Fruits", "Pza", 0, null);
        await ProductService.AddAsync(apple);

        using (var ctx = CreateContext())
        {
            var kitAProd = new ProductEntity { Id = "KITA", Barcode = "222", Name = "Kit A", Cost = 0m, Price = 0m, Stock = 0, Category = "Combo", Unit = "Pza", IsKit = true };
            ctx.Products.Add(kitAProd);

            var kitA = new ItemKitEntity { Id = "KITA-DEF", ParentProductId = "KITA", KitBarcode = "222", Name = "Kit A", Price = 5m };
            kitA.Components.Add(new ItemKitItemEntity { Id = "COMP-A1", ItemKitId = "KITA-DEF", ProductId = "APPLE", Quantity = 1.0 });
            ctx.ItemKits.Add(kitA);

            var kitBProd = new ProductEntity { Id = "KITB", Barcode = "333", Name = "Kit B", Cost = 0m, Price = 0m, Stock = 0, Category = "Combo", Unit = "Pza", IsKit = true };
            ctx.Products.Add(kitBProd);

            var kitB = new ItemKitEntity { Id = "KITB-DEF", ParentProductId = "KITB", KitBarcode = "333", Name = "Kit B", Price = 6m };
            kitB.Components.Add(new ItemKitItemEntity { Id = "COMP-B1", ItemKitId = "KITB-DEF", ProductId = "KITA", Quantity = 1.0 });
            ctx.ItemKits.Add(kitB);

            await ctx.SaveChangesAsync();
        }

        var item = new SaleItemSnapshotDto("KITB", "Kit B", 6.0, 2.0, 1.0, "Pza", "Combo", 0.0, 6.0);
        var saleDto = new SaleDto(
            Id: "SALE-NESTED-KIT",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item],
            Total: 6.0,
            TotalCost: 2.0,
            Profit: 4.0,
            PaidAmount: 6.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        await _saleService.SaveAsync(saleDto);

        using (var ctx = CreateContext())
        {
            var appleDb = await ctx.Products.FindAsync("APPLE");
            Assert.Equal(9.0, appleDb!.Stock);
        }
    }

    [Fact]
    public async Task ProcessSaleAsync_ShouldThrowOnCircularKitDependency()
    {
        using (var ctx = CreateContext())
        {
            var kitAProd = new ProductEntity { Id = "KITA-CIRC", Barcode = "444", Name = "Kit A Circ", Cost = 0m, Price = 0m, Stock = 0, Category = "Combo", Unit = "Pza", IsKit = true };
            ctx.Products.Add(kitAProd);

            var kitA = new ItemKitEntity { Id = "KITACIRC-DEF", ParentProductId = "KITA-CIRC", KitBarcode = "444", Name = "Kit A Circ", Price = 5m };
            kitA.Components.Add(new ItemKitItemEntity { Id = "COMP-C1", ItemKitId = "KITACIRC-DEF", ProductId = "KITA-CIRC", Quantity = 1.0 });
            ctx.ItemKits.Add(kitA);
            await ctx.SaveChangesAsync();
        }

        var item = new SaleItemSnapshotDto("KITA-CIRC", "Kit A Circ", 5.0, 2.0, 1.0, "Pza", "Combo", 0.0, 5.0);
        var saleDto = new SaleDto(
            Id: "SALE-CIRC-KIT",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item],
            Total: 5.0,
            TotalCost: 2.0,
            Profit: 3.0,
            PaidAmount: 5.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _saleService.SaveAsync(saleDto);
        });
    }

    [Fact]
    public async Task ProcessPartialReturnAsync_ShouldNotRestockAndLogShrinkage_WhenProductIsNotInGoodCondition()
    {
        // 1. Setup Product
        var product = new ProductDto("SHRINK-PROD", "777", "Damaged Item", 4.0, 10.0, 8.0, 5, 10, "Botanas", "Pza", 0, null);
        await ProductService.AddAsync(product);

        // 2. Perform Sale
        var item = new SaleItemSnapshotDto("SHRINK-PROD", "Damaged Item", 10.0, 4.0, 2, "Pza", "Botanas", 0.0, 20.0);
        var saleDto = new SaleDto(
            Id: "SALE-SHRINK-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item],
            Total: 20.0,
            TotalCost: 8.0,
            Profit: 12.0,
            PaidAmount: 20.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );
        var savedSale = await _saleService.SaveAsync(saleDto);

        // Check stock after sale (10 - 2 = 8)
        var prodAfterSale = await ProductService.GetByIdAsync("SHRINK-PROD");
        Assert.Equal(8.0, prodAfterSale!.Stock);

        // 3. Process return with isProductInGoodCondition = false using savedSale.Id
        var result = await _saleService.ProcessPartialReturnAsync(
            savedSale.Id,
            "SHRINK-PROD",
            1.0,
            "Rotura",
            "Efectivo",
            isProductInGoodCondition: false
        );

        Assert.True(result);

        // 4. Verify Stock was NOT incremented (remains 8.0)
        var prodAfterReturn = await ProductService.GetByIdAsync("SHRINK-PROD");
        Assert.Equal(8.0, prodAfterReturn!.Stock);

        // 5. Verify SpyAuditService captured the shrinkage log
        var shrinkageLog = _auditSpy.CapturedLogs
            .FirstOrDefault(l => l.EntityId == "SHRINK-PROD" && l.Reason.Contains("Devolución de producto dañado/mermado"));
        Assert.NotNull(shrinkageLog);
        Assert.Equal(4m, shrinkageLog.FinancialImpact); // Cost = 4.0 * Qty = 1.0 => 4.0
    }

    [Fact]
    public async Task CloseAsync_ShouldCreateShiftMovementForShortage_WhenActualBalanceIsLowerThanExpected()
    {
        var shiftService = new ShiftService(CreateContext());

        // 1. Open shift
        var activeShift = await shiftService.OpenAsync(100m); // opening balance = 100

        // 2. Perform cash sale
        var product = new ProductDto("CASH-CUT-PROD", "888", "Ice Cream", 5.0, 10.0, 8.0, 5, 20, "Botanas", "Pza", 0, null);
        await ProductService.AddAsync(product);

        var item = new SaleItemSnapshotDto("CASH-CUT-PROD", "Ice Cream", 10.0, 5.0, 10, "Pza", "Botanas", 0.0, 100.0);
        var saleDto = new SaleDto(
            Id: "SALE-CASH-CUT-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [item],
            Total: 100.0,
            TotalCost: 50.0,
            Profit: 50.0,
            PaidAmount: 100.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );
        await _saleService.SaveAsync(saleDto);

        // Expected balance: opening (100) + sale (100) = 200
        // 3. Close shift declaring only 180 (shortage of 20)
        var closedShift = await shiftService.CloseAsync(activeShift.Id, 180m);

        Assert.Equal(200m, closedShift.ExpectedBalance);
        Assert.Equal(180m, closedShift.ActualBalance);
        Assert.Equal(-20m, closedShift.Diff);

        // 4. Verify ShiftMovement was added for the shortage
        using (var ctx = CreateContext())
        {
            var movements = await ctx.ShiftMovements.ToListAsync();
            var shortageMovement = movements.FirstOrDefault(m => m.ShiftId == activeShift.Id && m.Description.Contains("Faltante de Caja"));
            Assert.NotNull(shortageMovement);
            Assert.Equal(20m, shortageMovement.Amount);
            Assert.True(shortageMovement.IsOutflow);
        }
    }

    [Fact]
    public async Task RechargeAsync_ShouldIncreaseGiftcardBalanceAndRecordShiftMovement()
    {
        var giftcardService = new GiftcardService(CreateContext());

        // 1. Create card
        await giftcardService.CreateCardAsync("GIFT-999", 50m, null);
        var card = await giftcardService.GetByCardNumberAsync("GIFT-999");
        Assert.NotNull(card);

        // Add active shift to prevent foreign key failure
        using (var ctx = CreateContext())
        {
            var shift = new ShiftEntity
            {
                Id = "SHIFT-TEST-01",
                OpeningBalance = 100m,
                IsOpen = 1,
                StartTime = DateTimeOffset.UtcNow.ToString("o")
            };
            ctx.Shifts.Add(shift);
            await ctx.SaveChangesAsync();
        }

        // 2. Recharge card with 150.0 cash under active shift
        await giftcardService.RechargeAsync(card.Id, 150.0m, Ticketfy.Core.Enums.PaymentMethod.Efectivo, "SHIFT-TEST-01");

        // 3. Verify balance (50 + 150 = 200)
        var updatedCard = await giftcardService.GetByCardNumberAsync("GIFT-999");
        Assert.NotNull(updatedCard);
        Assert.Equal(200m, updatedCard.Balance);

        // 4. Verify ShiftMovement created for the recharge inflow
        using (var ctx = CreateContext())
        {
            var movements = await ctx.ShiftMovements.ToListAsync();
            var rechargeMovement = movements.FirstOrDefault(m => m.ShiftId == "SHIFT-TEST-01" && m.Description.Contains("Recarga Monedero"));
            Assert.NotNull(rechargeMovement);
            Assert.Equal(150m, rechargeMovement.Amount);
            Assert.False(rechargeMovement.IsOutflow);
        }
    }

    [Fact]
    public void EscPosPrinterService_ShouldEncodeSpanishCharactersToCp858Bytes()
    {
        // 1. Register encoding provider
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // 2. Fetch CP858 encoding
        var printerEncoding = System.Text.Encoding.GetEncoding(858);
        Assert.NotNull(printerEncoding);

        // 3. Test conversion of "ñáéíóú"
        var text = "ñáéíóú";
        var bytes = printerEncoding.GetBytes(text);

        Assert.Equal(6, bytes.Length);
        // Under CP858: ñ is 0xA4, á is 0xA0, é is 0x82, í is 0xA1, ó is 0xA2, ú is 0xA3
        Assert.Equal(0xA4, bytes[0]);
        Assert.Equal(0xA0, bytes[1]);
        Assert.Equal(0x82, bytes[2]);
        Assert.Equal(0xA1, bytes[3]);
        Assert.Equal(0xA2, bytes[4]);
        Assert.Equal(0xA3, bytes[5]);
    }

    [Fact]
    public async Task BackupService_ShouldCreateBackupSuccessfully()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), $"TicketfyBackupTest_{Guid.NewGuid():N}");
        string databaseDirectory = Path.Combine(testRoot, "Database");
        string backupDirectory = Path.Combine(testRoot, "Backups");
        Directory.CreateDirectory(databaseDirectory);
        string dbPath = Path.Combine(databaseDirectory, "ticketfy.db");

        try
        {
            var connectionBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Password = Ticketfy.Services.Security.SecurityManager.GetMasterKey(),
                Pooling = false
            };
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE backup_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO backup_probe(value) VALUES ('ok');";
                await command.ExecuteNonQueryAsync();
                await connection.CloseAsync();
            }

            var backupService = new Ticketfy.Services.Implementations.BackupService(dbPath, backupDirectory);
            var result = await backupService.CreateZCutBackupAsync("SHIFT-TEST");

            Assert.True(result);
            var files = Directory.GetFiles(backupDirectory, "backup_ZCut_SHIFT-TEST_*.db");
            var backupFile = Assert.Single(files);

            var backupBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = backupFile,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Password = Ticketfy.Services.Security.SecurityManager.GetMasterKey(),
                Pooling = false
            };
            await using (var backupConnection = new Microsoft.Data.Sqlite.SqliteConnection(backupBuilder.ConnectionString))
            {
                await backupConnection.OpenAsync();
                await using var verifyCommand = backupConnection.CreateCommand();
                verifyCommand.CommandText = "SELECT value FROM backup_probe LIMIT 1;";
                Assert.Equal("ok", await verifyCommand.ExecuteScalarAsync());
                await backupConnection.CloseAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EscPosPrinterService_ShouldSupportGracefulDisposeAsync()
    {
        var printerService = new Ticketfy.Services.Implementations.EscPosPrinterService();

        // Simply dispose and check no exceptions are thrown
        await printerService.DisposeAsync();
    }

    [Fact]
    public void DateTimeExtensions_ShouldConvertBetweenUtcAndMexicoCityTimezone()
    {
        // 2026-08-10 05:30:00 UTC = 2026-08-09 23:30:00 America/Mexico_City
        var utcDate = new DateTime(2026, 8, 10, 5, 30, 0, DateTimeKind.Utc);
        var localDate = Ticketfy.Core.Helpers.DateTimeExtensions.ToBusinessLocalTime(utcDate);

        Assert.Equal(2026, localDate.Year);
        Assert.Equal(8, localDate.Month);
        Assert.Equal(9, localDate.Day);
        Assert.Equal(23, localDate.Hour);
        Assert.Equal(30, localDate.Minute);

        var utcBack = Ticketfy.Core.Helpers.DateTimeExtensions.ToBusinessUtcTime(localDate);
        Assert.Equal(utcDate, utcBack);
    }

    [Fact]
    public async Task GetCashierPerformanceReportAsync_ShouldFilterByDateRangeAndCalculateAccurately()
    {
        // 1. Setup product
        var product = new ProductDto("PROD-PERF", "123", "Soda", 5.0, 10.0, 0, 0, 100, "Bebidas", "Pza", 0, null);
        await ProductService.AddAsync(product);

        // 2. Setup user/cashier in DB
        using (var ctx = CreateContext())
        {
            ctx.Users.Add(new UserEntity
            {
                Id = Guid.NewGuid(),
                FullName = "Carlos Cajero",
                Username = "carlos",
                Role = Ticketfy.Core.Enums.UserRole.Cajero,
                IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        // 3. Create 2 sales: one inside date range, one outside date range
        var dateInside = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var dateOutside = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

        var item = new SaleItemSnapshotDto("PROD-PERF", "Soda", 10.0, 5.0, 2, "Pza", "Bebidas", 0.0, 20.0);

        var saleInside = new SaleDto(
            Id: "SALE-PERF-IN",
            Date: dateInside.ToString("o"),
            Items: [item],
            Total: 20.0,
            TotalCost: 10.0,
            Profit: 10.0,
            PaidAmount: 20.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        var saleOutside = new SaleDto(
            Id: "SALE-PERF-OUT",
            Date: dateOutside.ToString("o"),
            Items: [item],
            Total: 20.0,
            TotalCost: 10.0,
            Profit: 10.0,
            PaidAmount: 20.0,
            ChangeAmount: 0.0,
            PaymentMethod: "Cash",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null
        );

        await _saleService.SaveAsync(saleInside);
        await _saleService.SaveAsync(saleOutside);

        // 4. Query performance report with date range [2026-08-10 to 2026-08-20]
        var startFilter = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var endFilter = new DateTime(2026, 8, 20, 23, 59, 59, DateTimeKind.Utc);

        var report = await _saleService.GetCashierPerformanceReportAsync(startFilter, endFilter, defaultCommissionPct: 5.0m);

        Assert.NotNull(report);
        Assert.Single(report);

        var cashierMetrics = report[0];
        Assert.Equal("Carlos Cajero", cashierMetrics.CashierName);
        Assert.Equal(1, cashierMetrics.TicketCount);
        Assert.Equal(20m, cashierMetrics.TotalRevenue);
        Assert.Equal(20m, cashierMetrics.AverageTicketValue);
        Assert.Equal(1m, cashierMetrics.EarnedCommissionAmount); // 5% of $20 = $1.00
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

