using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task PredictiveCandidates_FindAvailableProductAfterInactiveEmptyAndInvalidRecipes()
    {
        await using var db = Db();
        db.Products.Add(new ProductEntity { Id = "INACTIVE", Name = "Inactive", IsActive = false, Stock = 20 });
        db.Products.Add(new ProductEntity { Id = "EMPTY", Name = "Empty", Stock = 0 });
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "INACTIVE", Frecuencia = 1001 });
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "EMPTY", ProductoB = "SOURCE", Frecuencia = 1000 });
        for (int i = 0; i < 70; i++)
        {
            string id = $"INVALID-KIT-{i:D4}";
            db.Products.Add(new ProductEntity { Id = id, Name = id, IsKit = true, Stock = 50 });
            db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = id, Frecuencia = 900 - i });
        }
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "P", ProductoB = "SOURCE", Frecuencia = 1 });
        await db.SaveChangesAsync();
        var paging = new PredictivePageObserver();
        var observed = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(paging).Options;
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(observed), _actor);
        Assert.Equal("P", (await service.GetTopCorrelatedProductAsync("SOURCE", ["SOURCE"]))?.Id);
        Assert.Equal(3, paging.Pages);
    }

    [Fact]
    public async Task PredictiveRestock_DoesNotHydrateTheWholeProductCatalogToReturnTwentyAlerts()
    {
        await using (var db = Db())
        {
            for (int i = 0; i < 10001; i++)
            {
                string id = $"DEMAND-{i:D5}";
                db.Products.Add(new ProductEntity { Id = id, Name = id, Stock = i % 4 });
                db.InventoryMovements.Add(new InventoryMovementEntity
                { ProductId = id, SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -28m });
            }
            await db.SaveChangesAsync();
        }
        var observer = new PredictiveMaterializationObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(options), _actor);
        var alerts = await service.GetUrgentRestockAlertsAsync();
        Assert.Equal(20, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(0m, a.DaysRemaining));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"DEMAND-{i * 4:D5}"), alerts.Select(a => a.ProductId));
        Assert.Equal(0, observer.Products);
        Assert.Equal(0, observer.Movements);
    }

    [Fact]
    public async Task PredictiveRestock_FutureDatedMovementsDoNotInventDemand()
    {
        await using var db = Db();
        db.InventoryMovements.Add(new InventoryMovementEntity
        {
            ProductId = "P",
            SaleLineId = "SYNTHETIC",
            Reason = "SALE",
            Quantity = -280m,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(10).ToString("O")
        });
        await db.SaveChangesAsync();
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        Assert.Empty(await service.GetUrgentRestockAlertsAsync());
    }

    [Fact]
    public async Task PredictiveCandidates_KeepCorrelationAndAvailabilityInOneSnapshot()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var setup = new AppDbContext(options))
            {
                setup.Products.Add(new ProductEntity { Id = "Q", Name = "Available", Price = 15m, Stock = 10 });
                setup.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "Q", Frecuencia = 3 });
                await setup.SaveChangesAsync();
            }
            var observer = new PredictiveConcurrentWrite(async () =>
            {
                await using var writer = new AppDbContext(options);
                var row = await writer.Products.SingleAsync(); row.Stock = 0; row.Price = 50m;
                await writer.SaveChangesAsync();
            });
            var observed = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options;
            var service = new PredictiveIntelligenceService(new TestDbContextFactory(observed), actor);
            var before = await service.GetTopCorrelatedProductAsync("SOURCE", []);
            Assert.True(observer.Committed);
            Assert.NotNull(before); Assert.Equal(15m, before.Price); Assert.Equal(10d, before.Stock);
            Assert.Null(await new PredictiveIntelligenceService(new TestDbContextFactory(options), actor).GetTopCorrelatedProductAsync("SOURCE", []));
        });
    }

    private sealed class PredictiveMaterializationObserver : IMaterializationInterceptor
    {
        public int Products { get; private set; }
        public int Movements { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is ProductEntity) Products++;
            if (entity is InventoryMovementEntity) Movements++;
            return entity;
        }
    }

    [Fact]
    public async Task PredictiveCandidates_DoNotDoubleCountLegacyDirectionsAndBreakTiesDeterministically()
    {
        await using var db = Db();
        db.Products.AddRange(new ProductEntity { Id = "Q", Name = "Other", Stock = 1 },
            new ProductEntity { Id = "SOURCE", Name = "Source", Stock = 1 });
        db.CoOccurrences.AddRange(
            new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "P", Frecuencia = 5 },
            new CoOccurrenceEntity { ProductoA = "P", ProductoB = "SOURCE", Frecuencia = 5 },
            new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "Q", Frecuencia = 7 },
            new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "SOURCE", Frecuencia = 999 });
        await db.SaveChangesAsync();
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        Assert.Equal("Q", (await service.GetTopCorrelatedProductAsync("SOURCE", []))?.Id);
        (await db.CoOccurrences.SingleAsync(c => c.ProductoB == "Q")).Frecuencia = 5;
        await db.SaveChangesAsync();
        Assert.Equal("P", (await service.GetTopCorrelatedProductAsync("SOURCE", []))?.Id);
        Assert.Equal("Q", (await service.GetTopCorrelatedProductAsync("SOURCE", ["P", "P"]))?.Id);
        Assert.Null(await service.GetTopCorrelatedProductAsync("SOURCE", ["P", "Q"]));
        (await db.CoOccurrences.SingleAsync(c => c.ProductoB == "Q")).Frecuencia = 0;
        await db.SaveChangesAsync();
        Assert.Null(await service.GetTopCorrelatedProductAsync("SOURCE", ["P"]));
    }

    [Fact]
    public async Task PredictiveCandidates_UseKitComponentAvailabilityAndRedactCashierCosts()
    {
        await using var db = Db();
        db.Products.Add(new ProductEntity { Id = "KIT", Name = "Virtual", IsKit = true, Stock = 0, Cost = 10m });
        db.ItemKits.Add(new ItemKitEntity { Id = "RECIPE", ParentProductId = "KIT", Name = "Virtual" });
        db.ItemKitItems.Add(new ItemKitItemEntity { ItemKitId = "RECIPE", ProductId = "P", Quantity = 2 });
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "KIT", Frecuencia = 5 });
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        var suggestion = await service.GetTopCorrelatedProductAsync("SOURCE", []);
        Assert.NotNull(suggestion); Assert.Equal("KIT", suggestion.Id); Assert.Equal(10d, suggestion.Stock);
        Assert.Equal(0m, suggestion.Cost); Assert.False(suggestion.CostsVisible);
        (await db.Products.SingleAsync(p => p.Id == "P")).Stock = 0;
        await db.SaveChangesAsync();
        Assert.Null(await service.GetTopCorrelatedProductAsync("SOURCE", []));
    }

    [Fact]
    public async Task PredictiveReads_EnforceInputBudgetAndCancellationWithoutPartialAnswers()
    {
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTopCorrelatedProductAsync(" ", []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetTopCorrelatedProductAsync("P", null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTopCorrelatedProductAsync("P", Enumerable.Repeat("Q", 1001).ToList()));
        Assert.Null(await service.GetTopCorrelatedProductAsync("P", Enumerable.Repeat("Q", 1000).ToList()));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetTopCorrelatedProductAsync("P", [], stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetUrgentRestockAlertsAsync(stop.Token));
    }

    [Fact]
    public async Task PredictiveRestock_WindowIncludesNetReturnsAndExcludesOldFutureAndUnrelatedMoves()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var clock = new FixedTestClock(now);
        await using var db = Db();
        (await db.Products.SingleAsync()).Stock = 27;
        db.InventoryMovements.AddRange(
            new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -280m, CreatedAtUtc = clock.GetUtcNow().AddDays(-28).ToString("O") },
            new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "RETURN", Quantity = 28m, CreatedAtUtc = clock.GetUtcNow().ToString("O") },
            new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "ADJUSTMENT", Quantity = 252m, CreatedAtUtc = clock.GetUtcNow().ToString("O") },
            new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "RETURN", Quantity = 252m, CreatedAtUtc = clock.GetUtcNow().AddTicks(1).ToString("O") },
            new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "RETURN", Quantity = 252m, CreatedAtUtc = clock.GetUtcNow().AddDays(-28).AddTicks(-1).ToString("O") });
        await db.SaveChangesAsync();
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor, clock);
        Assert.Equal(3m, Assert.Single(await service.GetUrgentRestockAlertsAsync()).DaysRemaining);
        (await db.Products.SingleAsync()).Stock = 28;
        await db.SaveChangesAsync();
        Assert.Empty(await service.GetUrgentRestockAlertsAsync());
        (await db.Products.SingleAsync()).Stock = -1;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetUrgentRestockAlertsAsync());
    }

    [Fact]
    public async Task PredictiveRestock_ConcurrentSaleCannotMixStockAndConsumption()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var seed = new AppDbContext(options))
            {
                seed.Products.Add(new ProductEntity { Id = "P", Name = "Physical", Stock = 2 });
                seed.InventoryMovements.Add(new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -28m });
                await seed.SaveChangesAsync();
            }
            var concurrent = new PredictiveConcurrentWrite(async () =>
            {
                await using var writer = new AppDbContext(options);
                (await writer.Products.SingleAsync()).Stock = 30;
                writer.InventoryMovements.Add(new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "RETURN", Quantity = 28m });
                await writer.SaveChangesAsync();
            }, "inventory_movements");
            var observed = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(concurrent).Options;
            var service = new PredictiveIntelligenceService(new TestDbContextFactory(observed), actor);
            Assert.Equal(2m, Assert.Single(await service.GetUrgentRestockAlertsAsync()).DaysRemaining);
            Assert.True(concurrent.Committed);
            Assert.Empty(await new PredictiveIntelligenceService(new TestDbContextFactory(options), actor).GetUrgentRestockAlertsAsync());
        });
    }

    private sealed class PredictivePageObserver : DbCommandInterceptor
    {
        public int Pages { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"co_ocurrencia\"", StringComparison.Ordinal))
            {
                Pages++;
                Assert.NotNull(command.Transaction); Assert.Contains("LIMIT", command.CommandText);
                Assert.Contains(command.Parameters.Cast<DbParameter>(), p => p.Value is int value && value == 32);
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PredictiveReads_SqliteUsesDateWindowAndBothCorrelationDirectionsIndexes()
    {
        await using (var db = Db())
        {
            db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "P", Frecuencia = 3 });
            db.InventoryMovements.Add(new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -280m });
            await db.SaveChangesAsync();
        }
        var observer = new PredictiveQueryCapture();
        var observed = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(observed), _actor);
        Assert.Single(await service.GetUrgentRestockAlertsAsync());
        Assert.NotNull(await service.GetTopCorrelatedProductAsync("SOURCE", []));
        var plans = new List<string>();
        foreach (var query in observer.Queries)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
            foreach (var parameter in query.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) plans.Add(reader.GetString(3));
        }
        Assert.Contains(plans, p => p.Contains("IX_inventory_movements_CreatedAtUtc_ProductId", StringComparison.Ordinal));
        Assert.Contains(plans, p => p.Contains("IX_co_ocurrencia_producto_b_producto_a", StringComparison.Ordinal));
        Assert.Contains(plans, p => p.Contains("sqlite_autoindex_co_ocurrencia_1", StringComparison.Ordinal));
    }

    private sealed class PredictiveQueryCapture : DbCommandInterceptor
    {
        public List<(string Sql, List<(string Name, object Value)> Parameters)> Queries { get; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"inventory_movements\"", StringComparison.Ordinal) ||
                command.CommandText.Contains("\"co_ocurrencia\"", StringComparison.Ordinal))
                Queries.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value!)).ToList()));
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PredictiveIndexes_MigrationIsRepeatableAndPreservesPostedMoneyStockAndFrequency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261020114735_DurableRecommendationWork";
        await db.Database.MigrateAsync();
        var actor = CommercialTestSession.Create(db);
        db.Products.Add(new ProductEntity { Id = "P", Barcode = "SYNTHETIC", Name = "Physical", Price = 10m, Cost = 3m, Stock = 20 });
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "P", ProductoB = "Q", Frecuencia = 7 });
        await db.SaveChangesAsync();
        var sales = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), actor);
        await sales.SaveAsync(Sale("INDEX-MIGRATION"));
        var original = await db.InventoryMovements.AsNoTracking().SingleAsync();
        await migrator.MigrateAsync(previous);
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var indexes = await db.Database.SqlQueryRaw<string>(
            """SELECT sql AS Value FROM sqlite_master WHERE type='index' AND name IN ('IX_inventory_movements_CreatedAtUtc_ProductId','IX_co_ocurrencia_producto_b_producto_a')""").ToListAsync();
        Assert.Equal(2, indexes.Count); Assert.Contains(indexes, sql => sql.Contains("WHERE \"SaleLineId\" IS NOT NULL", StringComparison.Ordinal));
        await migrator.MigrateAsync(previous); // This migration drops only its indexes, never documents or rows.
        await db.Database.MigrateAsync();
        var current = await db.InventoryMovements.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, current.Id); Assert.Equal(original.Quantity, current.Quantity);
        Assert.Equal(original.PostingSequence, current.PostingSequence); Assert.Equal(original.CreatedAtUtc, current.CreatedAtUtc);
        Assert.Equal(20m, (await db.Sales.AsNoTracking().SingleAsync()).Total);
        Assert.Equal(20m, (await db.SalePayments.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(18d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
        Assert.Equal(7, (await db.CoOccurrences.AsNoTracking().SingleAsync()).Frecuencia);
    }

    [Fact]
    public async Task PredictiveRestock_FractionalConsumptionKeepsDecimalPrecision()
    {
        await using var db = Db();
        var product = await db.Products.SingleAsync(); product.IsBulk = true; product.Stock = 0.001;
        db.InventoryMovements.Add(new InventoryMovementEntity
        { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -0.014m });
        await db.SaveChangesAsync();
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        Assert.Equal(2m, Assert.Single(await service.GetUrgentRestockAlertsAsync()).DaysRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PredictiveReads_CancellationDuringSqlDisposesSnapshotAndDoesNotReturnPartialResults(bool restock)
    {
        await using (var db = Db())
        {
            db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "SOURCE", ProductoB = "P", Frecuencia = 3 });
            db.InventoryMovements.Add(new InventoryMovementEntity { ProductId = "P", SaleLineId = "SYNTHETIC", Reason = "SALE", Quantity = -280m });
            await db.SaveChangesAsync();
        }
        using var stop = new CancellationTokenSource();
        var observer = new PredictiveCancelRead(stop, restock ? "inventory_movements" : "co_ocurrencia");
        var observed = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new PredictiveIntelligenceService(new TestDbContextFactory(observed), _actor);
        if (restock) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetUrgentRestockAlertsAsync(stop.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetTopCorrelatedProductAsync("SOURCE", [], stop.Token));
        Assert.True(stop.IsCancellationRequested);
        // The same disposable database/connection remains usable after cancellation.
        var normal = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        Assert.Single(await normal.GetUrgentRestockAlertsAsync());
        Assert.NotNull(await normal.GetTopCorrelatedProductAsync("SOURCE", []));
    }

    private sealed class PredictiveCancelRead(CancellationTokenSource stop, string table) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal)) stop.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PredictiveConcurrentWrite(Func<Task> write, string table = "co_ocurrencia") : DbCommandInterceptor
    {
        public bool Committed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Committed && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal))
            { await write(); Committed = true; }
            return result;
        }
    }
}
