using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [Theory]
    [InlineData("12345678901234567890")]
    [InlineData("passwordpassword")]
    [InlineData("TicketfyTicketfy")]
    [InlineData("administrador123456")]
    public async Task PasswordPolicy_CommonLongSecretsAreRejectedBeforeCreatingAccount(string secret)
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(Guid.NewGuid().ToString(), "Synthetic", "candidate", "CAJERO", secret, null));
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task PasswordTransition_WeakLegacyProofCannotReturnAnAuthenticatedUser()
    {
        await using var db = Db();
        db.Users.Add(new UserEntity
        {
            Username = "legacy-weak",
            FullName = "Synthetic",
            PasswordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old"))),
            PinCode = "4321"
        });
        await db.SaveChangesAsync();
        Assert.Null(await new UserService(db).AuthenticateAsync("legacy-weak", "old"));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("legacy-weak", "4321"));
    }

    [Fact]
    public async Task PasswordTransition_LegacyPinRequiresPasswordPolicyValidationFirst()
    {
        await using var db = Db();
        Assert.Null(await new UserRepository(db).ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await new UserService(db, quickAccess: _quickAccess).AuthenticateAsync("administrator", Password));
        Assert.NotNull(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
    }

    [Theory]
    [InlineData("department-manager", "department-manager2026!")]
    [InlineData("department-manager", "DEPARTMENT-MANAGER")]
    [InlineData("candidate", "correct horse battery staple")]
    [InlineData("candidate", "qwertyuiopasdfghjkl")]
    [InlineData("candidate", "aaaaaaaaaaaaaaaa")]
    [InlineData("candidate", "ééééééééééééééé")]
    [InlineData("candidate", "🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲🌲")]
    [InlineData("candidate", " passwordpassword ")]
    [InlineData("candidate", " administrador123 ")]
    [InlineData("candidate", "12345678901234567890123")]
    [InlineData("candidate", "e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301")]
    public async Task PasswordPolicy_ExpectedWholeSecretsAndShortUnicodeAreRejected(string username, string secret)
    {
        await using var db = Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserService(db, operations: _actor)
            .SaveAsync(Guid.NewGuid().ToString(), "Synthetic", username, "CAJERO", secret, null));
        Assert.Single(await db.Users.ToListAsync());
    }

    [Theory]
    [InlineData("Mi password está entre las hojas de otoño")]
    [InlineData("  Brisa-marina-taza-azul  ")]
    [InlineData("e\u0301 Luna distante, faro azul")]
    public async Task PasswordPolicy_DoesNotBanSubstringsOrTransformLiteralSecrets(string secret)
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        var id = Guid.NewGuid();
        await service.SaveAsync(id.ToString(), "Synthetic", "candidate", "CAJERO", secret, null);
        var created = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.True(SecurityManager.VerifyPassword(secret, created.PasswordHash));
        Assert.NotNull((await service.SignInAsync("candidate", secret)).User);
        if (secret.Trim() != secret)
            Assert.Null((await service.SignInAsync("candidate", secret.Trim())).User);
        if (secret.Normalize(NormalizationForm.FormC) != secret)
            Assert.Null((await service.SignInAsync("candidate", secret.Normalize(NormalizationForm.FormC))).User);
        Assert.Equal(PasswordPolicy.CurrentVersion, created.PasswordPolicyVersion);
    }

    [Fact]
    public async Task PasswordTransition_WeakProofRevokesCommercialAccessAndCannotApproveByAnyEntryPoint()
    {
        await using var db = Db();
        string hash = await MakePrimaryWeakAsync(db);
        var service = new UserService(db, operations: _actor);
        var result = await service.SignInAsync("administrator", "old");
        Assert.Null(result.User);
        Assert.True(result.PasswordChangeRequired);
        Assert.True((await service.SignInAsync("administrator", "old")).PasswordChangeRequired);
        Assert.False(await service.VerifyCredentialAsync(CommercialTestSession.UserId.ToString(), "old"));
        Assert.False(await service.VerifyManagerCredentialAsync(CommercialTestSession.UserId.ToString(), "old"));
        Assert.False(await service.VerifyManagerCredentialAsync(CommercialTestSession.UserId.ToString(), "4321", true));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("administrator", "4321"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _actor.RequireAsync(db, "inventory.view", false));
        var stored = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(-1, stored.PasswordPolicyVersion);
        Assert.Equal(hash, stored.PasswordHash); // Weak proof is not rehashed into a seemingly accepted credential.
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "PASSWORD_UPGRADE_REQUIRED").ToListAsync());
        Assert.Empty(await db.Sales.ToListAsync());
    }

    [Fact]
    public async Task PasswordTransition_OwnerRotationIsAtomicAuditedAndInvalidatesOldPin()
    {
        await using var db = Db();
        await MakePrimaryWeakAsync(db);
        var service = new UserService(db);
        Assert.True(await service.UpgradeOwnPasswordAsync(" ADMINISTRATOR ", "old", Password));
        var stored = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(CommercialTestSession.UserId, stored.Id);
        Assert.True(stored.IsActive);
        Assert.Equal("ADMINISTRADOR", stored.RoleString);
        Assert.Equal(PasswordPolicy.CurrentVersion, stored.PasswordPolicyVersion);
        Assert.Empty(stored.PasswordHint);
        Assert.Empty(stored.PinCode);
        Assert.False(SecurityManager.NeedsRehash(stored.PasswordHash));
        Assert.True(SecurityManager.VerifyPassword(Password, stored.PasswordHash));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("administrator", "4321"));
        Assert.False(await service.UpgradeOwnPasswordAsync("administrator", "old", "Second-distinct-test-secret"));
        Assert.Null(await service.AuthenticateAsync("administrator", "old"));
        Assert.NotNull(await service.AuthenticateAsync("administrator", Password));
        var journal = Assert.Single(await db.TransactionJournal.ToListAsync());
        Assert.Equal("PASSWORD_UPGRADED_BY_OWNER", journal.EventType);
        Assert.Equal(stored.Id.ToString(), journal.CashierUserId);
        string serialized = JsonSerializer.Serialize(journal);
        Assert.DoesNotContain(Password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.PasswordHash, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", journal.Details, StringComparison.Ordinal);
        Assert.Equal(0, (await db.CredentialAttempts.AsNoTracking().SingleAsync()).Failures);
    }

    [Fact]
    public async Task PasswordTransition_UsesSharedDurableBudgetAndDoesNotReviveInactiveOrMissingUsers()
    {
        await using var db = Db();
        await MakePrimaryWeakAsync(db);
        var clock = new CredentialClock();
        var service = new UserService(db, clock: clock);
        Assert.False(await service.UpgradeOwnPasswordAsync("administrator", "wrong", Password));
        Assert.Null(await service.AuthenticateAsync("administrator", "wrong"));
        Assert.False(await service.VerifyCredentialAsync(CommercialTestSession.UserId.ToString(), "0000", true));
        Assert.False(await service.VerifyManagerCredentialAsync(CommercialTestSession.UserId.ToString(), "wrong"));
        Assert.False(await service.UpgradeOwnPasswordAsync("administrator", "wrong", Password));
        Assert.False(await new UserService(db, clock: clock).UpgradeOwnPasswordAsync("administrator", "old", Password));
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "AUTHENTICATION_LOCKED").ToListAsync());
        clock.Utc = clock.Utc.AddMinutes(5);
        Assert.True(await service.UpgradeOwnPasswordAsync("administrator", "old", Password));
        await db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        Assert.False(await service.UpgradeOwnPasswordAsync("administrator", Password, "Another-synthetic-passphrase"));
        Assert.False(await service.UpgradeOwnPasswordAsync("missing", Password, "Another-synthetic-passphrase"));
        Assert.False(await service.UpgradeOwnPasswordAsync("", Password, "Another-synthetic-passphrase"));
        Assert.False(await service.UpgradeOwnPasswordAsync(new string('u', 129), Password, "Another-synthetic-passphrase"));
        Assert.False(await service.UpgradeOwnPasswordAsync("\ud800", Password, "Another-synthetic-passphrase"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpgradeOwnPasswordAsync("administrator", Password, Password));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpgradeOwnPasswordAsync("administrator", Password, "administrator123"));
        Assert.Single(await db.Users.ToListAsync());
        Assert.True(SecurityManager.VerifyPassword(Password, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash));
    }

    [Fact]
    public async Task PasswordTransition_RenamingAccountRequiresRevalidationAndCannotEnablePinForExpectedPassword()
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor, quickAccess: _quickAccess);
        string id = Guid.NewGuid().ToString(), initialPassword = "operator-identifier-2026";
        await service.SaveAsync(id, "Synthetic", "cashier-account", "CAJERO", initialPassword, "2468");
        Assert.NotNull(await service.AuthenticateAsync("cashier-account", initialPassword));
        Assert.NotNull(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("cashier-account", "2468"));
        await service.SaveAsync(id, "Synthetic", initialPassword, "CAJERO", null, null);
        Assert.Equal(0, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id))).PasswordPolicyVersion);
        Assert.Null(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync(initialPassword, "2468"));
        Assert.True((await service.SignInAsync(initialPassword, initialPassword)).PasswordChangeRequired);
        Assert.True(await service.UpgradeOwnPasswordAsync(initialPassword, initialPassword, Password));
        Assert.NotNull(await service.AuthenticateAsync(initialPassword, Password));
        Assert.Null(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync(initialPassword, "2468"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordTransition_JournalFailureRollsBackHashPinPolicyAndAttemptState(bool rotation)
    {
        await using var db = Db();
        string hash = await MakePrimaryWeakAsync(db);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailPasswordJournal()).Options;
        var failing = new UserService(new TestDbContextFactory(options));
        if (rotation)
            await Assert.ThrowsAsync<IOException>(() => failing.UpgradeOwnPasswordAsync("administrator", "old", Password));
        else
            await Assert.ThrowsAsync<IOException>(() => failing.SignInAsync("administrator", "old"));
        var unchanged = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(hash, unchanged.PasswordHash);
        Assert.Equal(0, unchanged.PasswordPolicyVersion);
        Assert.Equal("4321", unchanged.PinCode);
        Assert.Equal("legacy hint", unchanged.PasswordHint);
        Assert.Empty(await db.CredentialAttempts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task PasswordTransition_ConcurrentOwnerRotationsHaveOneWinnerAndSurviveReopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-password-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                db.Users.Add(new UserEntity { Username = "concurrent-owner", FullName = "Synthetic", PasswordHash = SecurityManager.HashPassword("old"), PinCode = "4321" });
                await db.SaveChangesAsync();
            }
            string[] alternatives = ["First-synthetic-replacement", "Second-synthetic-replacement"];
            var results = await Task.WhenAll(alternatives.Select(next => Task.Run(async () =>
            {
                await using var context = new AppDbContext(options);
                return await new UserService(context).UpgradeOwnPasswordAsync("concurrent-owner", "old", next);
            })));
            Assert.Single(results, result => result);
            await using var reopened = new AppDbContext(options);
            var stored = await reopened.Users.SingleAsync();
            Assert.Empty(stored.PinCode);
            Assert.Equal(PasswordPolicy.CurrentVersion, stored.PasswordPolicyVersion);
            Assert.True(SecurityManager.VerifyPassword(alternatives[Array.IndexOf(results, true)], stored.PasswordHash));
            Assert.False(SecurityManager.VerifyPassword(alternatives[Array.IndexOf(results, false)], stored.PasswordHash));
            Assert.Single(await reopened.TransactionJournal.Where(j => j.EventType == "PASSWORD_UPGRADED_BY_OWNER").ToListAsync());
            Assert.NotNull(await new UserService(reopened).AuthenticateAsync("concurrent-owner", alternatives[Array.IndexOf(results, true)]));
        }
        finally
        {
            // Exact, test-owned files only; no directory traversal or recursive cleanup.
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task PasswordTransition_MigrationPreservesLegacySecretsAndReapplicationDoesNotErasePolicyOrAttempts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020053205_PersistentLedgerPagingSequence");
        string hash = SecurityManager.HashPassword("old");
        Guid id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO usuarios (id,nombre,username,rol,role_string,password_hash,pin_checador_hash,estatus,password_hint)
            VALUES ({id},'Synthetic','old-owner',2,'CAJERO',{hash},'4321',1,'');
            """);
        await migrator.MigrateAsync();
        var legacy = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(hash, legacy.PasswordHash);
        Assert.Equal("4321", legacy.PinCode);
        Assert.Equal(0, legacy.PasswordPolicyVersion);
        var service = new UserService(db);
        Assert.True((await service.SignInAsync("old-owner", "old")).PasswordChangeRequired);
        Assert.Null(await service.AuthenticateAsync("old-owner", "wrong"));
        await migrator.MigrateAsync();
        Assert.Equal(-1, (await db.Users.AsNoTracking().SingleAsync()).PasswordPolicyVersion);
        Assert.Equal(1, (await db.CredentialAttempts.AsNoTracking().SingleAsync()).Failures);
        Assert.True(await service.UpgradeOwnPasswordAsync("old-owner", "old", Password));
        await migrator.MigrateAsync();
        var upgraded = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(PasswordPolicy.CurrentVersion, upgraded.PasswordPolicyVersion);
        Assert.Empty(upgraded.PinCode);
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261020053205_PersistentLedgerPagingSequence"));
        Assert.NotNull(await service.AuthenticateAsync("old-owner", Password));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<string> MakePrimaryWeakAsync(AppDbContext db)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old")));
        await db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, hash).SetProperty(x => x.PasswordPolicyVersion, 0)
            .SetProperty(x => x.PasswordHint, "legacy hint"));
        db.ChangeTracker.Clear();
        return hash;
    }

    private sealed class FailPasswordJournal : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TransactionJournalEntryEntity>()
                .Any(e => e.State == EntityState.Added && e.Entity.EventType.StartsWith("PASSWORD_", StringComparison.Ordinal)))
                throw new IOException("Synthetic password journal write failure.");
            return ValueTask.FromResult(result);
        }
    }
}
