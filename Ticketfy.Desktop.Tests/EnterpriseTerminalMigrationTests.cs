using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseTerminalMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_DowngradeIsReversibleOnlyWithoutVerifiedEvidence(bool verified)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        db.PaymentTerminalOperations.Add(new()
        {
            Id = "DECLINED",
            ExternalReference = "DECLINED",
            IdempotencyKey = "KEY-DECLINED",
            Amount = 20m,
            Status = IntegrationOperationStatus.FailedPermanent,
            NoFundsConfirmed = verified
        });
        await db.SaveChangesAsync();
        var migrator = db.GetService<IMigrator>();
        const string before = "20261020134104_IndexCheckoutRecoveryDecisions";
        if (verified) await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(before));
        else { await migrator.MigrateAsync(before); await db.Database.MigrateAsync(); }
        var row = await db.PaymentTerminalOperations.AsNoTracking().SingleAsync();
        Assert.Equal(verified, row.NoFundsConfirmed); Assert.Equal(20m, row.Amount);
        Assert.False(row.FundsObserved); Assert.Null(row.ProviderEvidenceHash);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task TerminalEvidence_UpgradePreservesKnownFundsWithoutInventingProviderDatesAndBlocksLossyDowngrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261020134104_IndexCheckoutRecoveryDecisions";
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO payment_terminal_operations
                (id, external_reference, idempotency_key, terminal_id, amount, status, provider_status, created_at_utc, updated_at_utc, attempt_count)
            VALUES ('A','A','KEY-A','T',2000,3,'processed','2026-10-20T12:00:00Z','2026-10-20T12:00:00Z',1),
                   ('B','B','KEY-B','T',2000,5,'refunded','2026-10-20T12:00:00Z','2026-10-20T12:00:00Z',1),
                   ('C','C','KEY-C','T',2000,0,NULL,'2026-10-20T12:00:00Z','2026-10-20T12:00:00Z',1);
            """);
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        var evidence = await db.PaymentTerminalOperations.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        Assert.True(evidence[0].FundsObserved); Assert.True(evidence[1].FundsObserved); Assert.False(evidence[2].FundsObserved);
        Assert.All(evidence, o => { Assert.Equal(20m, o.Amount); Assert.False(o.NoFundsConfirmed); Assert.Null(o.ProviderUpdatedAtUtc); Assert.Null(o.ProviderEvidenceHash); });
        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(before));
        Assert.Equal(2, await db.PaymentTerminalOperations.CountAsync(o => o.FundsObserved));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Sales.ToListAsync());
    }
}
