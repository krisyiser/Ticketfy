using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed class ItemKitServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly ItemKitService _service;
    private readonly ProductService _productService;

    public ItemKitServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
        _service = new ItemKitService(_context, CommercialTestSession.Create(_context));
        _productService = new ProductService(_context, CommercialTestSession.Create(_context));
    }

    [Fact]
    public async Task SaveAsync_ShouldCreateComboProductAndKitAndPromotion()
    {
        // 1. Create 2 ingredient products
        var p1 = new ProductDto("PROD-1", "111", "Leche Lala 1L", 15.0, 25.0, 20.0, 5, 50, "Lácteos", "Pza", 0, null);
        var p2 = new ProductDto("PROD-2", "222", "Coca Cola 600ml", 10.0, 18.0, 15.0, 5, 50, "Bebidas", "Pza", 0, null);
        await _productService.AddAsync(p1);
        await _productService.AddAsync(p2);

        // 2. Create Kit with draft items
        var items = new List<ItemKitItemDto>
        {
            new(Guid.NewGuid().ToString(), string.Empty, "PROD-1", "Leche Lala 1L", 1.0),
            new(Guid.NewGuid().ToString(), string.Empty, "PROD-2", "Coca Cola 600ml", 1.0)
        };

        var success = await _service.SaveAsync(
            Guid.NewGuid().ToString(),
            "4536434354",
            "paquetecagues",
            40m,
            "Incluye café y dona",
            items
        );

        Assert.True(success);

        var savedProductEntity = await _context.Products.FirstOrDefaultAsync(p => p.Barcode == "4536434354");
        Assert.NotNull(savedProductEntity);
        Assert.Equal("paquetecagues", savedProductEntity.Name);
        Assert.Equal(40m, savedProductEntity.Price);
        Assert.Equal("Promociones", savedProductEntity.Category);
        Assert.True(savedProductEntity.IsKit);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
