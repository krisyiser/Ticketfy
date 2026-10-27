using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Audit;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseAuditStreamingTests
{
    private static readonly string TestKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task AuditVerification_StreamsUntrackedChainAndCancellationStopsBeforeMaterializingTheRest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options;
        await using (var db = new AuditDbContext(options)) await db.Database.EnsureCreatedAsync();
        var service = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(options), TestKey);
        for (int i = 0; i < 501; i++)
            await service.LogAsync(new AuditLogEntity { EntityId = i.ToString(), Reason = "Synthetic audit entry", UserId = "synthetic-operator" });
        Assert.True(await service.VerifyIntegrityAsync());
        using var cancel = new CancellationTokenSource();
        var read = new CancelAuditRead(cancel);
        var observed = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).AddInterceptors(read).Options;
        var canceling = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(observed), TestKey);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceling.VerifyIntegrityAsync(cancel.Token));
        Assert.Equal(25, read.Count); Assert.Equal(0, read.Tracked);
        Assert.True(await service.VerifyIntegrityAsync());
        Assert.Equal(200, (await service.GetRecentLogsAsync(200)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetRecentLogsAsync(201));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetRecentLogsAsync(0));
        await using var verify = new AuditDbContext(options);
        Assert.Equal(501, await verify.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task AuditVerification_StillRejectsCorruptionWithoutModifyingTheChain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options;
        await using var db = new AuditDbContext(options); await db.Database.EnsureCreatedAsync();
        var service = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(options), TestKey);
        await service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC", Reason = "Original" });
        await db.AuditLogs.Where(a => a.Id == "SYNTHETIC").ExecuteUpdateAsync(a => a.SetProperty(x => x.Reason, "Corrupted synthetic entry"));
        Assert.False(await service.VerifyIntegrityAsync());
        Assert.Equal("Corrupted synthetic entry", (await db.AuditLogs.SingleAsync()).Reason);
    }

    private sealed class CancelAuditRead(CancellationTokenSource cancel) : IMaterializationInterceptor
    {
        public int Count { get; private set; }
        public int Tracked { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is AuditLogEntity)
            {
                Count++; Tracked = Math.Max(Tracked, materializationData.Context.ChangeTracker.Entries().Count());
                if (Count == 25) cancel.Cancel();
            }
            return entity;
        }
    }
}
