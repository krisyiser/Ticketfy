using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Audit;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseAuditEncodingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private DbContextOptions<AuditDbContext> Options => new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(_connection).Options;
    private AuditService Service => new(new AppDbContextFactoryImpl<AuditDbContext>(Options), _key);
    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = new AuditDbContext(Options);
        await db.Database.EnsureCreatedAsync();
    }
    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task AuditEncoding_ShiftingASeparatorAcrossFieldsCannotPreserveIntegrity()
    {
        await Service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC", OldValue = "one\u001ftwo", NewValue = "three" });
        Assert.True(await Service.VerifyIntegrityAsync());
        await using var db = new AuditDbContext(Options);
        await db.AuditLogs.ExecuteUpdateAsync(s => s.SetProperty(a => a.OldValue, "one").SetProperty(a => a.NewValue, "two\u001fthree"));
        Assert.False(await Service.VerifyIntegrityAsync());
    }

    [Fact]
    public async Task AuditEncoding_VersionOneWithoutAmbiguityIsPreservedAndCanPrecedeNewEntries()
    {
        var legacy = LegacyEntry();
        legacy.EntryHash = LegacyHash(legacy);
        await using (var db = new AuditDbContext(Options)) { db.Add(legacy); await db.SaveChangesAsync(); }
        Assert.True(await Service.VerifyIntegrityAsync());
        await Service.InitializeIntegrityAsync();
        await Service.LogAsync(new AuditLogEntity { Id = "NEW", Reason = "Synthetic current entry" });
        Assert.True(await Service.VerifyIntegrityAsync());
        await using var verify = new AuditDbContext(Options);
        var original = await verify.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == legacy.Id);
        var added = await verify.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == "NEW");
        Assert.Equal(legacy.EntryHash, original.EntryHash); Assert.Equal(1, original.SchemaVersion);
        Assert.Equal(2, added.SchemaVersion); Assert.Equal(legacy.EntryHash, added.PreviousHash);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public async Task AuditEncoding_AmbiguousLegacyOrUnknownVersionIsRejectedNotResigned(int version, bool delimiter)
    {
        var entry = LegacyEntry(); entry.SchemaVersion = version;
        if (delimiter) entry.OldValue = "one\u001ftwo";
        entry.EntryHash = LegacyHash(entry);
        await using (var db = new AuditDbContext(Options)) { db.Add(entry); await db.SaveChangesAsync(); }
        Assert.False(await Service.VerifyIntegrityAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync());
        await using var verify = new AuditDbContext(Options);
        Assert.Equal(entry.EntryHash, (await verify.AuditLogs.SingleAsync()).EntryHash);
    }

    [Fact]
    public async Task AuditEncoding_CurrentFormatDistinguishesNullSupervisorFromEmpty()
    {
        await Service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC", AuthorizedBySupervisorId = null });
        Assert.True(await Service.VerifyIntegrityAsync());
        await using var db = new AuditDbContext(Options);
        await db.AuditLogs.ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorizedBySupervisorId, ""));
        Assert.False(await Service.VerifyIntegrityAsync());
    }

    [Theory]
    [InlineData("UserId")]
    [InlineData("AuthorizedBySupervisorId")]
    [InlineData("ActionType")]
    [InlineData("RiskLevel")]
    [InlineData("EntityName")]
    [InlineData("EntityId")]
    [InlineData("OldValue")]
    [InlineData("NewValue")]
    [InlineData("FinancialImpact")]
    [InlineData("Reason")]
    [InlineData("TerminalName")]
    [InlineData("Timestamp")]
    [InlineData("PreviousHash")]
    [InlineData("SchemaVersion")]
    public async Task AuditEncoding_EverySignedFieldRemainsProtected(string field)
    {
        await Service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC", Reason = "雪 café 🧾\u001f" });
        Assert.True(await Service.VerifyIntegrityAsync());
        await using var db = new AuditDbContext(Options);
        var entry = await db.AuditLogs.SingleAsync();
        var property = db.Entry(entry).Property(field);
        property.CurrentValue = field switch
        {
            "ActionType" => Ticketfy.Core.Enums.AuditActionType.AuditIntegrityFailure,
            "RiskLevel" => Ticketfy.Core.Enums.RiskLevel.Warning,
            "FinancialImpact" => 0.01m,
            "SchemaVersion" => 1,
            _ => "SYNTHETIC CHANGE"
        };
        await db.SaveChangesAsync();
        Assert.False(await Service.VerifyIntegrityAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync());
    }

    [Fact]
    public async Task AuditEncoding_RejectsIllFormedUnicodeBeforeWritingAndCanThenAppendNormally()
    {
        await Assert.ThrowsAsync<EncoderFallbackException>(() =>
            Service.LogAsync(new AuditLogEntity { Id = "INVALID", TerminalName = "\ud800" }));
        await using var db = new AuditDbContext(Options);
        Assert.Empty(await db.AuditLogs.ToListAsync());
        await Service.LogAsync(new AuditLogEntity { Id = "VALID", Reason = "雪 café 🧾" });
        Assert.True(await Service.VerifyIntegrityAsync());
    }

    [Fact]
    public async Task AuditEncoding_IsInvariantAcrossCulturesAndIgnoresCallerSchemaVersion()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            await Service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC", SchemaVersion = 999, FinancialImpact = 12.34m });
            CultureInfo.CurrentCulture = new CultureInfo("es-MX");
            Assert.True(await Service.VerifyIntegrityAsync());
            await using var db = new AuditDbContext(Options);
            Assert.Equal(2, (await db.AuditLogs.SingleAsync()).SchemaVersion);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task AuditEncoding_UnsignedLegacyBootstrapUsesCurrentEncodingAndIsRepeatable()
    {
        var legacy = LegacyEntry(); legacy.Reason = "Imported legacy data, not a verified original signature";
        await using (var db = new AuditDbContext(Options)) { db.Add(legacy); await db.SaveChangesAsync(); }
        await Service.InitializeIntegrityAsync(allowLegacyBootstrap: true);
        await using var verify = new AuditDbContext(Options);
        var first = await verify.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(2, first.SchemaVersion);
        await Service.InitializeIntegrityAsync();
        Assert.True(await Service.VerifyIntegrityAsync());
        Assert.Equal(first.EntryHash, (await verify.AuditLogs.AsNoTracking().SingleAsync()).EntryHash);
    }

    [Fact]
    public async Task AuditInitialization_MissingSignaturesCannotBeSilentlyRepaired()
    {
        await Service.LogAsync(new AuditLogEntity { Id = "SYNTHETIC" });
        await using var db = new AuditDbContext(Options);
        await db.AuditLogs.ExecuteUpdateAsync(s => s.SetProperty(a => a.EntryHash, ""));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync(allowLegacyBootstrap: true));
        Assert.False(await Service.VerifyIntegrityAsync());
        Assert.Equal("", (await db.AuditLogs.SingleAsync()).EntryHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditInitialization_MixedUnsignedAndSignedHistoryCannotBeRewritten(bool allowLegacy)
    {
        var legacy = LegacyEntry();
        await using (var db = new AuditDbContext(Options)) { db.Add(legacy); await db.SaveChangesAsync(); }
        await Service.LogAsync(new AuditLogEntity { Id = "SIGNED" });
        await using var verify = new AuditDbContext(Options);
        string before = (await verify.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == "SIGNED")).EntryHash;
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync(allowLegacy));
        Assert.Equal(before, (await verify.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == "SIGNED")).EntryHash);
        Assert.Equal("", (await verify.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == legacy.Id)).EntryHash);
    }

    [Fact]
    public async Task AuditInitialization_PagesLegacyDataAndRollsBackAllBatchesOnFailure()
    {
        await using (var seed = new AuditDbContext(Options))
        {
            for (int i = 0; i < 501; i++)
            {
                var entry = LegacyEntry(); entry.Id = $"LEGACY-{i:D4}";
                seed.Add(entry);
            }
            await seed.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.InitializeIntegrityAsync());
        var guard = new AuditBatchGuard { FailSecondSave = true };
        var observed = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(_connection).AddInterceptors(guard).Options;
        var service = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(observed), _key);
        await Assert.ThrowsAsync<IOException>(() => service.InitializeIntegrityAsync(allowLegacyBootstrap: true));
        await using var verify = new AuditDbContext(Options);
        Assert.Equal(501, await verify.AuditLogs.CountAsync(a => a.EntryHash == "" && a.SchemaVersion == 1));
        Assert.Equal(0, await verify.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'audit_logs_no_%'").SingleAsync());
        guard.FailSecondSave = false;
        await service.InitializeIntegrityAsync(allowLegacyBootstrap: true);
        Assert.InRange(guard.MaximumTracked, 1, 200);
        Assert.True(await Service.VerifyIntegrityAsync());
        Assert.Equal(501, await verify.AuditLogs.CountAsync(a => a.SchemaVersion == 2));
        await Service.InitializeIntegrityAsync(); // Entire signed chain: no rewriting protected rows.
        await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs"));
    }

    [Fact]
    public async Task AuditInitialization_CancellationRollsBackLegacyBootstrapAndAllowsExplicitRetry()
    {
        await using (var seed = new AuditDbContext(Options))
        {
            for (int i = 0; i < 201; i++) { var entry = LegacyEntry(); entry.Id = $"LEGACY-{i:D4}"; seed.Add(entry); }
            await seed.SaveChangesAsync();
        }
        using var stop = new CancellationTokenSource();
        var cancel = new AuditBatchGuard { CancelAfterFirstSave = stop };
        var observed = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(_connection).AddInterceptors(cancel).Options;
        var service = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(observed), _key);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InitializeIntegrityAsync(true, stop.Token));
        await using var verify = new AuditDbContext(Options);
        Assert.Equal(201, await verify.AuditLogs.CountAsync(a => a.EntryHash == ""));
        await Service.InitializeIntegrityAsync(allowLegacyBootstrap: true);
        Assert.True(await Service.VerifyIntegrityAsync());
    }

    private sealed class AuditBatchGuard : SaveChangesInterceptor
    {
        private int _saves;
        public bool FailSecondSave { get; set; }
        public CancellationTokenSource? CancelAfterFirstSave { get; init; }
        public int MaximumTracked { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaximumTracked = Math.Max(MaximumTracked, eventData.Context!.ChangeTracker.Entries().Count());
            if (++_saves == 2 && FailSecondSave) throw new IOException("Synthetic second audit batch failure");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_saves == 1) CancelAfterFirstSave?.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private static AuditLogEntity LegacyEntry() => new()
    {
        Id = "LEGACY",
        Timestamp = "2026-01-01T00:00:00.0000000+00:00",
        UserId = "synthetic",
        TerminalName = "synthetic-terminal",
        OldValue = "before",
        NewValue = "after"
    };

    // Exact historical format, intentionally retained only for compatibility fixtures.
    private string LegacyHash(AuditLogEntity entry)
    {
        string canonical = string.Join("\u001f", entry.SchemaVersion, entry.Id, entry.Timestamp, entry.UserId,
            entry.AuthorizedBySupervisorId ?? "", (int)entry.ActionType, (int)entry.RiskLevel,
            entry.EntityName, entry.EntityId, entry.OldValue, entry.NewValue,
            entry.FinancialImpact.ToString("G29", CultureInfo.InvariantCulture), entry.Reason, entry.TerminalName, entry.PreviousHash);
        return Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(_key), Encoding.UTF8.GetBytes(canonical)));
    }
}
