using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Core.Startup;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage1TransactionalCoreTests
{
    [Theory]
    [InlineData("3.0.43", "20260801054956_InitialCreate")]
    [InlineData("3.0.44", "20260808183950_AddSecurityFields")]
    [InlineData("3.1.25", "20260817060006_AddSatProductCodes")]
    public async Task CommercialVersion_UpgradesToLatest_AndPreservesMoney(
        string commercialVersion,
        string migrationId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var context = new AppDbContext(options);
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(migrationId);

        await InsertLegacyProductAsync(connection);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        var upgraded = await context.Products.AsNoTracking().SingleAsync(p => p.Id == "UPGRADE-PRODUCT");
        Assert.Equal(12.34m, upgraded.Cost);
        Assert.Equal(19.99m, upgraded.Price);
        Assert.True(await context.Database.GetAppliedMigrationsAsync()
            .ContinueWith(t => t.Result.Contains("20261018111240_StabilizeTransactionalCore")));
        Assert.False(string.IsNullOrWhiteSpace(commercialVersion));
    }

    [Fact]
    public async Task LegacyCommercialDatabaseWithoutHistory_IsValidatedAdoptedAndUpgraded()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"ticketfy-legacy-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using (var legacy = new AppDbContext(options))
            {
                await legacy.Database.GetService<IMigrator>()
                    .MigrateAsync("20260817060006_AddSatProductCodes");
                await legacy.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");
            }

            await DatabaseInitializer.AdoptValidatedLegacyHistoryAsync(connectionString);

            await using var upgraded = new AppDbContext(options);
            await upgraded.Database.MigrateAsync();
            var applied = await upgraded.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20261018111240_StabilizeTransactionalCore", applied);
            Assert.True(await upgraded.CheckoutOperations.AnyAsync() == false);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RepeatedSale_ChangesStockCashAndFolioExactlyOnce()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddProductAsync(stock: 10);
        await fixture.AddOpenShiftAsync();
        var sale = CreateSale("sale-idempotent", quantity: 2, total: 20m);

        var first = await fixture.Service.SaveAsync(sale);
        var second = await fixture.Service.SaveAsync(sale);

        await using var verify = fixture.CreateContext();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await verify.Sales.CountAsync());
        Assert.Equal(8d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(1, await verify.ShiftMovements.CountAsync(m => m.ReferenceId == first.Id));
        Assert.Equal(20m, (await verify.Shifts.SingleAsync()).TotalCashSales);
        Assert.Equal(1, await verify.TransactionJournal.CountAsync(j => j.OperationId == "sale-idempotent"));
    }

    [Fact]
    public async Task CompetingSales_ForLastUnit_OnlyOneCanCommit()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"ticketfy-concurrency-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=10;Pooling=False")
                .Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Products.Add(Product(stock: 1));
                await setup.SaveChangesAsync();
            }

            var first = Service(options);
            var second = Service(options);
            var attempts = await Task.WhenAll(
                CaptureAsync(() => first.SaveAsync(CreateSale("stock-race-a", 1, 10m))),
                CaptureAsync(() => second.SaveAsync(CreateSale("stock-race-b", 1, 10m))));

            await using var verify = new AppDbContext(options);
            Assert.Single(attempts, success => success);
            Assert.Equal(1, await verify.Sales.CountAsync());
            Assert.Equal(0d, (await verify.Products.SingleAsync()).Stock);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AuthorizedPayment_IsRecoveredOnceAfterRestart()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddProductAsync(stock: 5);
        var sale = CreateSale("recover-authorized", 1, 10m) with { PaymentMethod = "Tarjeta", CashAmount = 0m, CardAmount = 10m };

        await fixture.Service.BeginCheckoutAsync(sale);
        using (var bankProof = fixture.CreateContext()) CommercialTestSession.ConfirmCard(bankProof, sale.OperationId!, 10m, "terminal-authorization-1");
        await fixture.Service.MarkPaymentAuthorizedAsync(sale.OperationId!, "terminal-authorization-1");

        var restartedService = Service(fixture.Options);
        Assert.Equal(1, await restartedService.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, await restartedService.RecoverInterruptedCheckoutsAsync());

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Sales.CountAsync());
        Assert.Equal(4d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(Ticketfy.Core.Enums.CheckoutOperationState.Completed,
            (await verify.CheckoutOperations.SingleAsync()).State);
    }

    [Fact]
    public async Task RepeatedReturn_RestoresStockAndRefundExactlyOnce()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddProductAsync(stock: 10);
        var saved = await fixture.Service.SaveAsync(CreateSale("return-sale", 2, 20m));

        Assert.True(await fixture.Service.ProcessPartialReturnAsync(
            saved.Id, "PRODUCT-1", 1, "Devolución de prueba", "Efectivo", true, "return-once"));
        Assert.True(await fixture.Service.ProcessPartialReturnAsync(
            saved.Id, "PRODUCT-1", 1, "Devolución de prueba", "Efectivo", true, "return-once"));

        await using var verify = fixture.CreateContext();
        Assert.Equal(9d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(1, await verify.Returns.CountAsync());
        Assert.Equal(10m, (await verify.Returns.SingleAsync()).TotalRefunded);
    }

    [Fact]
    public async Task RepeatedCancellation_RestoresInventoryAndDrawerExactlyOnce()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddProductAsync(stock: 10);
        await fixture.AddOpenShiftAsync();
        var sale = CreateSale("cancel-once", 2, 20m) with
        {
            PaidAmount = 30m,
            ChangeAmount = 10m,
            CashAmount = 20m
        };

        var saved = await fixture.Service.SaveAsync(sale);
        Assert.True(await fixture.Service.CancelSaleAsync(saved.Id, "Prueba de cancelación"));
        Assert.False(await fixture.Service.CancelSaleAsync(saved.Id, "Prueba de cancelación"));

        await using var verify = fixture.CreateContext();
        Assert.Equal(10d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(100m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(2, await verify.ShiftMovements.CountAsync());
        Assert.Equal(2, await verify.TransactionJournal.CountAsync());

        var shiftService = new ShiftService(verify, CommercialTestSession.Create(fixture.Options));
        var closed = await shiftService.CloseAsync("SHIFT-1", 100m);
        Assert.Equal(100m, closed.ExpectedBalance);
        Assert.Equal(0m, closed.Diff);
    }

    [Fact]
    public async Task RepeatedCustomerPayment_ReducesDebtAndMovesCashExactlyOnce()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddOpenShiftAsync();
        await using (var setup = fixture.CreateContext())
        {
            setup.Customers.Add(new CustomerEntity { Id = "CUSTOMER-1", Name = "Cliente", Debt = 100m, CreditLimit = 500m });
            await setup.SaveChangesAsync();
        }

        using (var baseline = fixture.CreateContext()) CommercialTestSession.OpeningDebt(baseline, "CUSTOMER-1", 100m);
        var service = new CustomerService(fixture.CreateContext(), operations: CommercialTestSession.Create(fixture.Options));
        Assert.True(await service.RegisterCustomerPaymentAsync("CUSTOMER-1", 40m, "Efectivo", "Prueba", "payment-once"));
        Assert.True(await service.RegisterCustomerPaymentAsync("CUSTOMER-1", 40m, "Efectivo", "Prueba", "payment-once"));

        await using var verify = fixture.CreateContext();
        Assert.Equal(60m, (await verify.Customers.SingleAsync()).Debt);
        Assert.Equal(1, await verify.CustomerPayments.CountAsync());
        Assert.Equal(1, await verify.ShiftMovements.CountAsync());
        Assert.Equal(1, await verify.TransactionJournal.CountAsync(j => j.OperationId == "payment-once"));
    }

    [Fact]
    public async Task CustomerPayment_AboveCurrentDebt_RollsBackCashAndAudit()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddOpenShiftAsync();
        await using (var setup = fixture.CreateContext())
        {
            setup.Customers.Add(new CustomerEntity { Id = "CUSTOMER-2", Name = "Cliente", Debt = 25m, CreditLimit = 500m });
            await setup.SaveChangesAsync();
        }

        var service = new CustomerService(fixture.CreateContext(), operations: CommercialTestSession.Create(fixture.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterCustomerPaymentAsync("CUSTOMER-2", 40m, "Efectivo", "Exceso", "payment-too-large"));

        await using var verify = fixture.CreateContext();
        Assert.Equal(25m, (await verify.Customers.SingleAsync()).Debt);
        Assert.Empty(await verify.CustomerPayments.ToListAsync());
        Assert.Empty(await verify.ShiftMovements.ToListAsync());
        Assert.Empty(await verify.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task InsufficientStock_RollsBackSaleDebtCashAndJournal()
    {
        await using var fixture = await TransactionFixture.CreateAsync();
        await fixture.AddProductAsync(stock: 1);
        await fixture.AddOpenShiftAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveAsync(CreateSale("must-rollback", 2, 20m)));

        await using var verify = fixture.CreateContext();
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Empty(await verify.ShiftMovements.ToListAsync());
        Assert.Empty(await verify.TransactionJournal.ToListAsync());
        Assert.Equal(1d, (await verify.Products.SingleAsync()).Stock);
    }

    private static async Task<bool> CaptureAsync(Func<Task<SaleDto>> action)
    {
        try { await action(); return true; }
        catch (Exception) { return false; }
    }

    private static async Task InsertLegacyProductAsync(SqliteConnection connection)
    {
        var columns = new List<(string Name, string Type, bool NotNull)>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(products);";
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO products ({string.Join(",", columns.Select(c => $"\"{c.Name}\""))}) VALUES ({string.Join(",", columns.Select((_, index) => $"$p{index}"))});";
        for (int index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            object value = column.Name switch
            {
                "id" => "UPGRADE-PRODUCT",
                "barcode" => "7500000000999",
                "name" => "Producto migrado",
                "cost" => 12.34d,
                "price" => 19.99d,
                "wholesalePrice" => 18.50d,
                "stock" => 10d,
                "category" => "General",
                "unit" => "Pza",
                "created_at" => "2026-01-01",
                "clave_sat" or "sat_product_code" => "01010101",
                "unidad_sat" or "sat_unit_code" => "H87",
                _ when !column.NotNull => DBNull.Value,
                _ when column.Type.Contains("INT", StringComparison.OrdinalIgnoreCase)
                    || column.Type.Contains("REAL", StringComparison.OrdinalIgnoreCase) => 0,
                _ => string.Empty
            };
            insert.Parameters.AddWithValue($"$p{index}", value);
        }
        await insert.ExecuteNonQueryAsync();
    }

    private static SaleService Service(DbContextOptions<AppDbContext> options) =>
        new(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), CommercialTestSession.Create(options));

    private static ProductEntity Product(double stock) => new()
    {
        Id = "PRODUCT-1",
        Barcode = "7500000000001",
        Name = "Producto",
        Cost = 5m,
        Price = 10m,
        Stock = stock,
        Category = "General",
        Unit = "Pza"
    };

    private static SaleDto CreateSale(string operationId, double quantity, decimal total) => new(
        Id: operationId,
        Date: DateTimeOffset.UtcNow.ToString("O"),
        Items: [new SaleItemSnapshotDto("PRODUCT-1", "Producto", 10m, 5m, quantity, "Pza", "General", 0m, total)],
        Total: total,
        TotalCost: 5m * (decimal)quantity,
        Profit: total - (5m * (decimal)quantity),
        PaidAmount: total,
        ChangeAmount: 0m,
        PaymentMethod: "Efectivo",
        CustomerId: null,
        IsCredit: false,
        IsCancelled: false,
        CancelledAt: null,
        CashAmount: total,
        OperationId: operationId);

    private sealed class TransactionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public DbContextOptions<AppDbContext> Options { get; }
        public SaleService Service { get; }

        private TransactionFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            Options = options;
            Service = Stage1TransactionalCoreTests.Service(options);
        }

        public static async Task<TransactionFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TransactionFixture(connection, options);
        }

        public AppDbContext CreateContext() => new(Options);

        public async Task AddProductAsync(double stock)
        {
            await using var context = CreateContext();
            context.Products.Add(Product(stock));
            await context.SaveChangesAsync();
        }

        public async Task AddOpenShiftAsync()
        {
            await using var context = CreateContext();
            var shift = await context.Shifts.SingleAsync(s => s.Id == "SHIFT-1");
            shift.OpeningBalance = 100m;
            shift.ExpectedBalance = 100m;
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
