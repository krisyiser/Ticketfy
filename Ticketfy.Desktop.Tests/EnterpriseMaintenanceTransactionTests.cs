using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Operations;
using Ticketfy.Core.Startup;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Audit;
using Microsoft.Data.Sqlite;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task MaintenanceGuard_AlertWorkerReportsSuspensionAndResumesAfterAuditVerification()
    {
        var health = new MaintenanceHealth();
        UseMaintenanceGuard(health);
        var clock = new ManualMaintenanceClock();
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.Changed += () =>
        {
            var state = health.GetSnapshot().Tasks.Single(x => x.Task == MaintenanceTask.StockAlerts).State;
            if (state == MaintenanceState.Suspended) suspended.TrySetResult();
            if (state == MaintenanceState.Healthy) recovered.TrySetResult();
        };
        await using var worker = new AlertBackgroundWorker(new TestDbContextFactory(_options), clock: clock, health: health);
        worker.Start();
        await suspended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(typeof(MaintenanceWriteBlockedException).FullName, worker.LastFailureType);
        health.Succeeded(MaintenanceTask.Audit); clock.Tick();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(worker.LastFailureType);
        await worker.DisposeAsync();
        Assert.Equal(MaintenanceState.Stopped, health.GetSnapshot().Tasks.Single(x => x.Task == MaintenanceTask.StockAlerts).State);
    }

    [Fact]
    public async Task MaintenanceGuard_RealChainCorruptionBlocksSaleAndReadOnlyRetryDoesNotRepairIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options;
        await using var auditDb = new AuditDbContext(options); await auditDb.Database.EnsureCreatedAsync();
        var audit = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(options),
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await audit.LogAsync(new AuditLogEntity { Id = "SYNTHETIC-HEALTH-AUDIT", Reason = "Original" });
        var health = new MaintenanceHealth(); UseMaintenanceGuard(health);
        await using var verification = new AuditVerificationCoordinator(audit, health);
        Assert.True(await verification.VerifyAsync());
        await auditDb.AuditLogs.ExecuteUpdateAsync(a => a.SetProperty(x => x.Reason, "Altered synthetic record"));
        var evidence = await auditDb.AuditLogs.AsNoTracking().SingleAsync();
        Assert.False(await verification.VerifyAsync());
        await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => Sales.SaveAsync(Sale("CORRUPT-AUDIT")));
        Assert.False(await verification.VerifyAsync());
        var after = await auditDb.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(evidence.EntryHash, after.EntryHash);
        Assert.Equal(evidence.Reason, after.Reason);
        await using var db = Db();
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
        Assert.Empty(await db.Sales.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceGuard_RejectsWritesButKeepsReadsAndRollbackAvailable(bool asynchronous)
    {
        var health = new MaintenanceHealth();
        UseMaintenanceGuard(health);
        await using var db = Db();
        await using (await ReadSnapshot.BeginAsync(db))
            Assert.Equal("Producto", (await db.Products.SingleAsync()).Name);
        db.Settings.Add(new SettingEntity { Key = "synthetic", Value = "never written" });
        if (asynchronous)
        {
            await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => db.SaveChangesAsync());
            await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => db.Products.ExecuteUpdateAsync(p => p.SetProperty(x => x.Stock, 0)));
            await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM products"));
        }
        else
        {
            Assert.Throws<MaintenanceWriteBlockedException>(() => db.SaveChanges());
            Assert.Throws<MaintenanceWriteBlockedException>(() => db.Products.ExecuteUpdate(p => p.SetProperty(x => x.Stock, 0)));
            Assert.Throws<MaintenanceWriteBlockedException>(() => db.Database.ExecuteSqlRaw("DELETE FROM products"));
        }
        Assert.Equal(20d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
        Assert.False(await db.Settings.AnyAsync(s => s.Key == "synthetic"));
        health.Succeeded(MaintenanceTask.Audit);
        if (asynchronous) await db.SaveChangesAsync(); else db.SaveChanges();
        Assert.True(await db.Settings.AnyAsync(s => s.Key == "synthetic"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceGuard_FailureBetweenWriteAndCommitRollsBackTheWholeTransaction(bool asynchronous)
    {
        var health = new MaintenanceHealth(); health.Succeeded(MaintenanceTask.Audit);
        UseMaintenanceGuard(health);
        await using (var db = Db())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (asynchronous) await db.Products.ExecuteUpdateAsync(p => p.SetProperty(x => x.Stock, 9));
            else db.Products.ExecuteUpdate(p => p.SetProperty(x => x.Stock, 9));
            health.Failed(MaintenanceTask.Audit);
            if (asynchronous) await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => tx.CommitAsync());
            else Assert.Throws<MaintenanceWriteBlockedException>(tx.Commit);
            if (asynchronous) await tx.RollbackAsync(); else tx.Rollback();
        }
        await using var verify = Db();
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
        health.Succeeded(MaintenanceTask.Audit);
        await using var committed = await verify.Database.BeginTransactionAsync();
        if (asynchronous) await committed.CommitAsync(); else committed.Commit();
    }

    [Fact]
    public async Task MaintenanceGuard_SaleFailureBeforeCommitDoesNotDuplicateMoneyStockOrFolioOnRetry()
    {
        var health = new MaintenanceHealth(); health.Succeeded(MaintenanceTask.Audit);
        var injected = new AuditFailureAfterSave(health);
        UseMaintenanceGuard(health, injected);
        int before;
        await using (var db = Db()) before = await db.FolioSequences.CountAsync();
        await Assert.ThrowsAsync<MaintenanceWriteBlockedException>(() => Sales.SaveAsync(Sale("HEALTH-ATOMIC")));
        await using (var db = Db())
        {
            Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
            Assert.Equal(0m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.SalePayments.ToListAsync());
            Assert.Empty(await db.InventoryMovements.ToListAsync());
            Assert.Empty(await db.CommandReceipts.ToListAsync());
            Assert.Empty(await db.TransactionJournal.ToListAsync());
            Assert.Equal(before, await db.FolioSequences.CountAsync());
        }
        health.Succeeded(MaintenanceTask.Audit);
        var first = await Sales.SaveAsync(Sale("HEALTH-ATOMIC"));
        var repeated = await Sales.SaveAsync(Sale("HEALTH-ATOMIC"));
        Assert.Equal(first.Id, repeated.Id);
        await using var verify = Db();
        Assert.Single(await verify.Sales.ToListAsync());
        Assert.Single(await verify.SalePayments.ToListAsync());
        Assert.Single(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(20m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
    }

    private void UseMaintenanceGuard(MaintenanceHealth health, params IInterceptor[] extra) =>
        _options = new DbContextOptionsBuilder<AppDbContext>(AppBootstrapper.CreateCommercialOptions(_connection.ConnectionString, health))
            .UseSqlite(_connection).AddInterceptors(extra).Options;
    private sealed class AuditFailureAfterSave(MaintenanceHealth health) : SaveChangesInterceptor
    {
        private bool _injected;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_injected) { _injected = true; health.Failed(MaintenanceTask.Audit, MaintenanceFailure.VerificationFailed); }
            return ValueTask.FromResult(result);
        }
    }
}
