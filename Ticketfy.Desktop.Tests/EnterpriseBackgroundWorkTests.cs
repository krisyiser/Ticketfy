using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task<SaleDto> RecommendationSaleAsync(string operation, int products)
    {
        await using var db = Db();
        for (int i = 0; i < products; i++)
            db.Products.Add(new ProductEntity { Id = $"REC-{i:0000}", Barcode = $"REC-{i:0000}", Name = $"Product {i}", Price = 10m, Cost = 3m, Stock = 20 });
        await db.SaveChangesAsync();
        return new(operation, "", Enumerable.Range(0, products).Select(i => new SaleItemSnapshotDto(
            $"REC-{i:0000}", $"Product {i}", 10m, 3m, 1, "Pza", "General", 0m, 10m)).ToList(),
            products * 10m, 0m, 0m, products * 10m, 0m, "Efectivo", null, false, false, null,
            CashAmount: products * 10m, OperationId: operation);
    }

    [Fact]
    public async Task RecommendationWork_IsCommittedWithSaleAndReplaysNeverEnqueueAnotherJob()
    {
        var sale = await RecommendationSaleAsync("RECOMMEND", 30);
        await Sales.SaveAsync(sale); await Sales.SaveAsync(sale);
        await using var db = Db(); Assert.Single(await db.RecommendationJobs.ToListAsync());
        var worker = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        Assert.True(await worker.ProcessBatchAsync(default));
        Assert.Equal(256, await db.CoOccurrences.CountAsync());
        Assert.False((await db.RecommendationJobs.SingleAsync()).IsCompleted);
        var reopened = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        Assert.True(await reopened.ProcessBatchAsync(default));
        Assert.False(await reopened.ProcessBatchAsync(default));
        Assert.Equal(435, await db.CoOccurrences.CountAsync());
        Assert.All(await db.CoOccurrences.ToListAsync(), row => Assert.Equal(1, row.Frecuencia));
        Assert.True((await db.RecommendationJobs.AsNoTracking().SingleAsync()).IsCompleted);
        Assert.Empty(await db.TransactionJournal.Where(j => j.EventType != "SALE_COMPLETED").ToListAsync());
        await worker.DisposeAsync(); await reopened.DisposeAsync();
    }

    [Fact]
    public async Task RecommendationWork_SingleProductTicketNeedsNoPairJob()
    {
        await Sales.SaveAsync(Sale("SINGLE"));
        await using var db = Db(); Assert.Empty(await db.RecommendationJobs.ToListAsync());
    }

    [Fact]
    public async Task RecommendationWork_FailedJobPublicationRollsBackMoneyAndInventory()
    {
        var sale = await RecommendationSaleAsync("NO-PARTIAL", 3);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailRecommendationSave()).Options;
        var sales = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor);
        await Assert.ThrowsAsync<IOException>(() => sales.SaveAsync(sale));
        await using var db = Db();
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.SalePayments.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync()); Assert.Empty(await db.RecommendationJobs.ToListAsync());
        Assert.All(await db.Products.ToListAsync(), p => Assert.Equal(20, p.Stock));
    }

    [Fact]
    public async Task RecommendationWork_FailedCursorWriteRollsBackCountersAndRetryStartsSameBatch()
    {
        await Sales.SaveAsync(await RecommendationSaleAsync("RETRY-PAIRS", 30));
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailRecommendationSave()).Options;
        await using var failing = new CoOccurrenceWorker(new(), new TestDbContextFactory(options));
        await Assert.ThrowsAsync<IOException>(() => failing.ProcessBatchAsync(default));
        await using var db = Db();
        Assert.Empty(await db.CoOccurrences.ToListAsync());
        var job = await db.RecommendationJobs.SingleAsync(); Assert.Equal(0, job.NextLeft); Assert.Equal(1, job.NextRight);
        await using var working = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        Assert.True(await working.ProcessBatchAsync(default)); Assert.True(await working.ProcessBatchAsync(default));
        Assert.Equal(435, await db.CoOccurrences.CountAsync());
        Assert.All(await db.CoOccurrences.ToListAsync(), c => Assert.Equal(1, c.Frecuencia));
    }

    [Fact]
    public async Task RecommendationWork_LostCommitResponseAndReopenDoNotDoubleCountPairs()
    {
        await Sales.SaveAsync(await RecommendationSaleAsync("LOST-PAIR-COMMIT", 30));
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new LoseRecommendationCommit()).Options;
        await using var failing = new CoOccurrenceWorker(new(), new TestDbContextFactory(options));
        await Assert.ThrowsAsync<IOException>(() => failing.ProcessBatchAsync(default));
        await using var db = Db(); Assert.Equal(256, await db.CoOccurrences.CountAsync());
        await using var reopened = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        Assert.True(await reopened.ProcessBatchAsync(default)); Assert.False(await reopened.ProcessBatchAsync(default));
        Assert.Equal(435, await db.CoOccurrences.CountAsync());
        Assert.All(await db.CoOccurrences.ToListAsync(), c => Assert.Equal(1, c.Frecuencia));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task RecommendationWork_InvalidFrequencyCannotWrapOrAdvanceCursor(int frequency)
    {
        await Sales.SaveAsync(await RecommendationSaleAsync("COUNTER-LIMIT", 3));
        await using var db = Db();
        db.CoOccurrences.Add(new() { ProductoA = "REC-0000", ProductoB = "REC-0001", Frecuencia = frequency }); await db.SaveChangesAsync();
        await using var worker = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ProcessBatchAsync(default));
        Assert.Equal(frequency, (await db.CoOccurrences.SingleAsync()).Frecuencia);
        Assert.Equal(0, (await db.RecommendationJobs.SingleAsync()).NextLeft);
    }

    [Fact]
    public async Task RecommendationWork_OneTicketDoesNotMultiplyCountsForDuplicateLinesAndLookupIsSymmetric()
    {
        var sale = await RecommendationSaleAsync("DUPLICATE-LINE", 2);
        sale = sale with { Items = [.. sale.Items, sale.Items[0]], Total = 30m, PaidAmount = 30m, CashAmount = 30m };
        await Sales.SaveAsync(sale);
        await using var worker = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        Assert.True(await worker.ProcessBatchAsync(default));
        await using var db = Db(); Assert.Equal(1, (await db.CoOccurrences.SingleAsync()).Frecuencia);
        var recommendations = new PredictiveIntelligenceService(new TestDbContextFactory(_options), _actor);
        Assert.Equal("REC-0001", (await recommendations.GetTopCorrelatedProductAsync("REC-0000", ["REC-0000"]))!.Id);
        Assert.Equal("REC-0000", (await recommendations.GetTopCorrelatedProductAsync("REC-0001", ["REC-0001"]))!.Id);
        Assert.Null(await recommendations.GetTopCorrelatedProductAsync("REC-0001", ["REC-0000", "REC-0001"]));
    }

    [Fact]
    public async Task StockAlertWorker_PagesPhysicalActiveProductsAndDoesNotDuplicateOnReplay()
    {
        await using var db = Db();
        db.Products.AddRange(Enumerable.Range(0, 451).Select(i => new ProductEntity
        { Id = $"ALERT-{i:0000}", Name = $"Low stock {i}", Stock = 1, MinStock = 2, IsActive = true }));
        db.Products.Add(new ProductEntity { Id = "INACTIVE-ALERT", Name = "Inactive", Stock = 0, MinStock = 5, IsActive = false });
        db.Products.Add(new ProductEntity { Id = "VIRTUAL-ALERT", Name = "Kit", Stock = 0, MinStock = 5, IsKit = true });
        await db.SaveChangesAsync();
        await using var worker = new AlertBackgroundWorker(new TestDbContextFactory(_options));
        await worker.RunOnceAsync(default); await worker.RunOnceAsync(default);
        Assert.Equal(451, await db.SystemAlerts.CountAsync());
        Assert.Equal(451, await db.SystemAlerts.Select(a => a.ProductId).Distinct().CountAsync());
        Assert.False(await db.SystemAlerts.AnyAsync(a => a.ProductId == "INACTIVE-ALERT" || a.ProductId == "VIRTUAL-ALERT"));
        await db.SystemAlerts.Where(a => a.ProductId == "ALERT-0000").ExecuteUpdateAsync(a => a.SetProperty(x => x.IsResolved, true));
        await worker.RunOnceAsync(default);
        Assert.Equal(452, await db.SystemAlerts.CountAsync());
        Assert.Single(await db.SystemAlerts.Where(a => a.ProductId == "ALERT-0000" && !a.IsResolved).ToListAsync());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(canceled.Token));
    }

    [Fact]
    public async Task StockAlertWorker_RepeatedProductInOneCheckoutCreatesOnlyOneAlert()
    {
        await using (var db = Db())
        {
            var product = await db.Products.SingleAsync(); product.MinStock = 20; await db.SaveChangesAsync();
        }
        var sale = Sale("ALERT-DUP") with
        {
            Items = [Sale("X").Items[0] with { Quantity = 1, TotalPrice = 10m },
            Sale("X").Items[0] with { Quantity = 1, TotalPrice = 10m }]
        };
        await Sales.SaveAsync(sale);
        await using var worker = new AlertBackgroundWorker(new TestDbContextFactory(_options)); await worker.RunOnceAsync(default);
        await using var verify = Db(); Assert.Single(await verify.SystemAlerts.ToListAsync());
    }

    [Fact]
    public async Task RecommendationMigration_PreservesLegacyCountersWithoutReenqueuingHistoricalSales()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261020083213_ProtectPromotionEditRevisions");
        db.CoOccurrences.Add(new() { ProductoA = "LEGACY-A", ProductoB = "LEGACY-B", Frecuencia = 7 }); await db.SaveChangesAsync();
        await migrator.MigrateAsync(); await migrator.MigrateAsync();
        Assert.Equal(7, (await db.CoOccurrences.SingleAsync()).Frecuencia);
        Assert.Empty(await db.RecommendationJobs.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261020083213_ProtectPromotionEditRevisions"));
    }

    private sealed class FailRecommendationSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<RecommendationJobEntity>().Any(e => e.State is EntityState.Added or EntityState.Modified)
                ? throw new IOException("Synthetic job write failure") : ValueTask.FromResult(result);
    }
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    public async Task RecommendationWork_InvalidDurableCursorIsNotSilentlyAcknowledged(int left, int right)
    {
        await Sales.SaveAsync(await RecommendationSaleAsync("BAD-CURSOR", 3));
        await using var db = Db();
        await db.RecommendationJobs.ExecuteUpdateAsync(j => j.SetProperty(x => x.NextLeft, left).SetProperty(x => x.NextRight, right));
        await using var worker = new CoOccurrenceWorker(new(), new TestDbContextFactory(_options));
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.ProcessBatchAsync(default));
        Assert.False((await db.RecommendationJobs.SingleAsync()).IsCompleted);
        Assert.Empty(await db.CoOccurrences.ToListAsync());
    }
    [Fact]
    public async Task RecommendationWork_ActualOwnedLoopProcessesDurableJobAndStopsAfterCommit()
    {
        await Sales.SaveAsync(await RecommendationSaleAsync("OWNED-LOOP", 3));
        var committed = new RecommendationCommitSignal();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(committed).Options;
        var queue = new CoOccurrenceQueue();
        await using var worker = new CoOccurrenceWorker(queue, new TestDbContextFactory(options));
        worker.Start(); worker.Start();
        await committed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 100; i++) queue.Signal();
        await worker.DisposeAsync();
        Assert.True(worker.Completion.IsCompletedSuccessfully);
        await using var db = Db();
        Assert.True((await db.RecommendationJobs.SingleAsync()).IsCompleted);
        Assert.Equal(3, await db.CoOccurrences.CountAsync());
        Assert.All(await db.CoOccurrences.ToListAsync(), pair => Assert.Equal(1, pair.Frecuencia));
    }

    [Fact]
    public async Task StockAlertWorker_FailedSecondPageRetainsFirstPageAndRetryCompletesWithoutDuplicates()
    {
        await using (var db = Db())
        {
            db.Products.AddRange(Enumerable.Range(0, 451).Select(i => new ProductEntity { Id = $"RETRY-{i:0000}", Name = "Low", Stock = 0, MinStock = 1 }));
            await db.SaveChangesAsync();
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailSecondAlertPage()).Options;
        await using var failing = new AlertBackgroundWorker(new TestDbContextFactory(options));
        await Assert.ThrowsAsync<IOException>(() => failing.RunOnceAsync(default));
        await using var verify = Db(); Assert.Equal(200, await verify.SystemAlerts.CountAsync());
        await using var reopened = new AlertBackgroundWorker(new TestDbContextFactory(_options));
        await reopened.RunOnceAsync(default);
        Assert.Equal(451, await verify.SystemAlerts.CountAsync());
        Assert.Equal(451, await verify.SystemAlerts.Select(a => a.ProductId).Distinct().CountAsync());
    }

    [Fact]
    public async Task BackgroundWork_TwoConnectionsCannotDuplicateAlertsOrPairBatches()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-workers-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            Ticketfy.Services.CommercialOperationContext actor;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync(); await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(db);
                db.Products.AddRange(Enumerable.Range(0, 30).Select(i => new ProductEntity
                { Id = $"WAL-{i:0000}", Barcode = $"WAL-{i:0000}", Name = "Physical", Price = 10m, Cost = 3m, Stock = 20, MinStock = 21 }));
                await db.SaveChangesAsync();
            }
            var factory = new TestDbContextFactory(options);
            var sale = new SaleDto("WAL-WORK", "", Enumerable.Range(0, 30).Select(i => new SaleItemSnapshotDto(
                $"WAL-{i:0000}", "Physical", 10m, 3m, 1, "Pza", "", 0m, 10m)).ToList(),
                300m, 0m, 0m, 300m, 0m, "Efectivo", null, false, false, null, CashAmount: 300m, OperationId: "WAL-WORK");
            await new SaleService(factory, new(), new SpyAuditService(), actor).SaveAsync(sale);
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await using var alerts = new AlertBackgroundWorker(factory); await alerts.RunOnceAsync(default);
                await using var pairs = new CoOccurrenceWorker(new(), factory); await pairs.ProcessBatchAsync(default);
            })));
            await using var verify = new AppDbContext(options);
            Assert.Equal(30, await verify.SystemAlerts.CountAsync());
            Assert.Equal(435, await verify.CoOccurrences.CountAsync());
            Assert.All(await verify.CoOccurrences.ToListAsync(), p => Assert.Equal(1, p.Frecuencia));
            Assert.True((await verify.RecommendationJobs.SingleAsync()).IsCompleted);
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private sealed class FailSecondAlertPage : SaveChangesInterceptor
    {
        private int _attempts;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SystemAlertEntity>().Any(e => e.State == EntityState.Added)
                && Interlocked.Increment(ref _attempts) == 2) throw new IOException("Synthetic second page failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RecommendationCommitSignal : DbTransactionInterceptor
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RecommendationJobEntity>().Any(j => j.Entity.IsCompleted)) Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
    private sealed class LoseRecommendationCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            throw new IOException("Synthetic lost recommendation commit acknowledgment");
    }
}
