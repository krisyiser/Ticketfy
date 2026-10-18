using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed class ProductServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly ProductService _service;

    public ProductServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
        _service = new ProductService(_context);
    }

    [Fact]
    public async Task AddAndGetByBarcode_ShouldReturnCorrectProduct()
    {
        var dto = new ProductDto(
            Id: "PROD-TEST-001",
            Barcode: "7501112223334",
            Name: "Test Beverage 500ml",
            Cost: 10.0,
            Price: 15.0,
            WholesalePrice: 12.0,
            WholesaleThreshold: 10,
            Stock: 50,
            Category: "Bebidas",
            Unit: "Pza",
            ExpiresSoon: 0,
            CreatedAt: DateTimeOffset.UtcNow.ToString("o")
        );

        await _service.AddAsync(dto);

        var retrieved = await _service.GetByBarcodeAsync("7501112223334");
        Assert.NotNull(retrieved);
        Assert.Equal("Test Beverage 500ml", retrieved.Name);
        Assert.Equal(15m, retrieved.Price);
        Assert.Equal(50, retrieved.Stock);
    }

    [Fact]
    public async Task UpdateProduct_ShouldModifyExistingRecord()
    {
        var dto = new ProductDto("PROD-TEST-002", "7509998887776", "Original Name", 5.0, 10.0, 8.0, 5, 20, "Abarrotes", "Pza", 0, null);
        await _service.AddAsync(dto);

        var updatedDto = dto with { Name = "Updated Name", Price = 12.5m, Stock = 30 };
        await _service.UpdateAsync(updatedDto);

        var retrieved = await _service.GetByIdAsync("PROD-TEST-002");
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Name", retrieved.Name);
        Assert.Equal(12.5m, retrieved.Price);
        Assert.Equal(30, retrieved.Stock);
    }

    [Fact]
    public async Task DeleteProduct_ShouldRemoveFromDatabase()
    {
        var dto = new ProductDto("PROD-TEST-003", "7500000000001", "To Delete", 5.0, 10.0, 0, 0, 10, "Abarrotes", "Pza", 0, null);
        await _service.AddAsync(dto);

        await _service.DeleteAsync("PROD-TEST-003");

        var retrieved = await _service.GetByIdAsync("PROD-TEST-003");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task CatalogBulkImportSearchAndClear_KeepInventoryConsistent()
    {
        var first = new ProductDto("P-BULK-1", "10001", "Café molido", 40m, 65m, 0m, 0, 8, "Cafetería", "Pza", 0, null);
        var second = new ProductDto("P-BULK-2", "10002", "Azúcar", 20m, 32m, 0m, 0, 12, "Abarrotes", "Pza", 0, null);
        await _service.BulkSaveAsync([first, second]);

        await _service.BulkSaveAsync([first with { Name = "Café premium", Stock = 15 }]);
        Assert.Equal(2, (await _service.GetAllAsync()).Count);
        Assert.Equal("Café premium", (await _service.GetByCategoryAsync("Cafetería")).Single().Name);
        Assert.Equal("P-BULK-1", (await _service.SearchFtsAsync("premium")).Single().Id);
        Assert.Equal(2, (await _service.SearchFtsAsync(" ")).Count);

        int imported = await _service.ImportFromCsvTextAsync(
            "barcode,name,cost,price,stock,category,unit\n10003,Leche,18.50,27.90,20,Lácteos,Pza");
        Assert.Equal(1, imported);
        Assert.Equal(27.90m, (await _service.GetByBarcodeAsync("10003"))!.Price);

        Assert.True(await _service.AdjustStockManuallyAsync("P-BULK-1", 9, "Conteo físico", "tester"));
        Assert.Equal(9, (await _service.GetByIdAsync("P-BULK-1"))!.Stock);
        Assert.Equal(0, await _service.ImportCsvAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv")));

        await _service.ClearInventoryAsync();
        Assert.Empty(await _service.GetAllAsync());
    }

    [Fact]
    public async Task DuplicateBarcode_IsRejectedForCreateAndUpdate()
    {
        var first = new ProductDto("P-DUP-1", "DUPLICATE", "Uno", 1m, 2m, 0m, 0, 1, "General", "Pza", 0, null);
        var second = new ProductDto("P-DUP-2", "OTHER", "Dos", 1m, 2m, 0m, 0, 1, "General", "Pza", 0, null);
        await _service.AddAsync(first);
        await _service.AddAsync(second);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddAsync(second with { Id = "P-DUP-3", Barcode = "DUPLICATE" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(second with { Barcode = "DUPLICATE" }));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
