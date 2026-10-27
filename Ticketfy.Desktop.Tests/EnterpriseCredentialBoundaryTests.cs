using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Repositories;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Security;
using Ticketfy.Core.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly CommercialOperationContext _actor;
    private readonly QuickAccessSession _quickAccess = new();
    private const string Password = "Synthetic-secret-for-test";
    private const string HashShapedLiteral = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
    public EnterpriseCredentialBoundaryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = Db();
        db.Database.EnsureCreated();
        _actor = CommercialTestSession.Create(db, openShift: false);
        var admin = db.Users.Single();
        admin.Username = "administrator";
        admin.FullName = "Same visible name";
        admin.PasswordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)));
        admin.PinCode = "4321"; // Legacy fixture, never a production default.
        db.SaveChanges();
    }
    private AppDbContext Db() => new(_options);

    [Fact]
    public async Task AdministrativeUserDirectory_RequiresAuthorizedSession()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new UserService(db).GetAllAsync());
        var cashier = new UserEntity { Id = Guid.NewGuid(), Username = "cashier", FullName = "Cashier", Role = UserRole.Cajero, RoleString = "CAJERO", IsActive = true };
        db.Users.Add(cashier); await db.SaveChangesAsync();
        var actor = CommercialTestSession.Create(db, openShift: false, userId: cashier.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new UserService(db, operations: actor).GetAllAsync());
        Assert.Equal(2, (await new UserService(db, operations: _actor).GetAllAsync()).Count);
    }

    [Fact]
    public void PublicUserContract_DoesNotExportCredentialHashes()
    {
        Assert.DoesNotContain(typeof(IUserService).GetMethods(), m => m.Name is "GetPasswordHashAsync" or "GetPinHashAsync");
    }

    [Fact]
    public async Task LastActiveAdministrator_CannotBeDemoted()
    {
        await using var db = Db();
        var users = new UserService(db, operations: _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(CommercialTestSession.UserId.ToString(),
            "Same visible name", "administrator", "CAJERO", null, null));
        var admin = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Equal("ADMINISTRADOR", admin.RoleString);
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task CredentialFailures_SurviveServiceRecreationAndShareIdentityAcrossPinEntryPoints()
    {
        await using var db = Db();
        string id = CommercialTestSession.UserId.ToString();
        var users = new UserService(db);
        for (int i = 0; i < 5; i++) Assert.False(await users.VerifyCredentialAsync(id, "0000", pin: true));
        Assert.False(await new UserService(db).VerifyCredentialAsync(id, "4321", pin: true));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("administrator", "4321"));
    }

    [Fact]
    public async Task PinAuthentication_DoesNotAcceptDisplayNameAsCredentialIdentity()
    {
        await using var db = Db();
        Assert.Null(await new UserRepository(db).ValidatePinAsync("Same visible name", "4321"));
    }

    [Fact]
    public async Task CredentialBudget_IsSharedByPasswordPinAndUsernameAndExpiresWithoutSleeping()
    {
        await using var db = Db();
        var clock = new CredentialClock();
        var service = new UserService(db, operations: _actor, clock: clock, quickAccess: _quickAccess);
        var repository = new UserRepository(db, clock: clock, quickAccess: _quickAccess);
        string id = CommercialTestSession.UserId.ToString();
        Assert.False(await service.VerifyCredentialAsync(id, "bad"));
        Assert.Null(await service.AuthenticateAsync(" ADMINISTRATOR ", "bad"));
        Assert.Null(await repository.ValidatePinAsync("administrator", "0000"));
        Assert.False(await service.VerifyManagerCredentialAsync(id, "bad"));
        Assert.False(await service.ConfirmCurrentAdministratorAsync("bad"));
        Assert.Null(await service.AuthenticateAsync("administrator", Password));
        Assert.Null(await repository.ValidatePinAsync("ADMINISTRATOR", "4321"));
        Assert.Equal(5, (await db.CredentialAttempts.SingleAsync()).Failures);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "AUTHENTICATION_LOCKED").ToListAsync());
        clock.Utc = clock.Utc.AddMinutes(5);
        Assert.NotNull(await service.AuthenticateAsync("administrator", Password));
        Assert.True(await service.ConfirmCurrentAdministratorAsync(Password));
        var current = await db.Users.AsNoTracking().SingleAsync();
        Assert.False(SecurityManager.NeedsRehash(current.PasswordHash));
        Assert.Equal(0, (await db.CredentialAttempts.AsNoTracking().SingleAsync()).Failures);
        clock.Utc = clock.Utc.AddSeconds(-1);
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        clock.Utc = clock.Utc.AddSeconds(1);
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.False(SecurityManager.PinNeedsRehash((await db.Users.AsNoTracking().SingleAsync()).PinCode));
    }

    [Fact]
    public async Task AdministrativeProfile_IsRedactedAndRequiresPermission_AndInactiveUsersCannotApprove()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new UserRepository(db).GetAdminProfileAsync());
        var profile = await new UserRepository(db, operations: _actor).GetAdminProfileAsync();
        Assert.NotNull(profile);
        Assert.Equal("administrator", profile.Username);
        Assert.DoesNotContain("Password", string.Join(",", profile.GetType().GetProperties().Select(p => p.Name)));
        var service = new UserService(db, operations: _actor);
        Assert.False(await service.VerifyCredentialAsync("invalid-id", Password));
        Assert.False(await service.VerifyManagerCredentialAsync("invalid-id", Password));
        Assert.Null(await service.AuthenticateAsync("administrator", new string('x', 1025)));
        Assert.Null(await service.AuthenticateAsync(new string('x', 129), Password));
        var user = await db.Users.SingleAsync();
        user.IsActive = false; await db.SaveChangesAsync();
        Assert.Null(await service.AuthenticateAsync("administrator", Password));
        Assert.False(await service.VerifyManagerCredentialAsync(user.Id.ToString(), "4321", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmCurrentAdministratorAsync(Password));
    }

    [Fact]
    public async Task UnknownAccountsDoNotCreateUnboundedAttemptRecords_AndFailedLoginClearsPriorIdentity()
    {
        await using var db = Db();
        var users = new UserService(db);
        for (int i = 0; i < 20; i++) Assert.Null(await users.AuthenticateAsync($"missing-{i}", Password));
        Assert.Single(await db.CredentialAttempts.ToListAsync());
        var auth = new AuthService(users);
        Assert.True(await auth.LoginAsync("administrator", Password));
        Assert.False(await auth.LoginAsync("administrator", "wrong"));
        Assert.False(auth.IsAuthenticated);
        Assert.Null(auth.CurrentUser);
    }

    [Fact]
    public async Task UserAdministration_RejectsNumericRolesAndDoesNotCountMislabelledCashiersAsAdministrators()
    {
        await using var db = Db();
        db.Users.Add(new UserEntity { Id = Guid.NewGuid(), Username = "legacy-cashier", FullName = "Legacy", Role = UserRole.Admin, RoleString = "CAJERO" });
        await db.SaveChangesAsync();
        var service = new UserService(db, operations: _actor);
        string id = CommercialTestSession.UserId.ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "Admin", "administrator", "0", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "Admin", "administrator", "CAJERO", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(id.ToUpperInvariant()));
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task CredentialBudget_PersistsAcrossClosedDatabaseAndConcurrentServiceInstances()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-credential-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(new UserEntity
                {
                    Id = Guid.NewGuid(),
                    Username = "durable",
                    FullName = "Synthetic",
                    PasswordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))),
                    IsActive = true
                });
                await setup.SaveChangesAsync();
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await using var context = new AppDbContext(options);
                return await new UserService(context).AuthenticateAsync("durable", "wrong");
            })));
            Assert.All(results, Assert.Null);
            await using var reopened = new AppDbContext(options);
            Assert.Null(await new UserService(reopened).AuthenticateAsync("durable", Password));
            Assert.Equal(5, (await reopened.CredentialAttempts.SingleAsync()).Failures);
            Assert.Single(await reopened.TransactionJournal.ToListAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CredentialMigration_PreservesAccountsAndDoesNotResetAttemptsWhenReapplied()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020022845_EnforceSingleActiveAttendance");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)));
        Guid oldUserId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO usuarios (id,nombre,username,rol,role_string,password_hash,pin_checador_hash,estatus,password_hint)
            VALUES ({oldUserId},'Synthetic','migration-user',2,'CAJERO',{hash},'',1,'');
            """);
        await migrator.MigrateAsync();
        Assert.Equal(hash, (await db.Users.SingleAsync()).PasswordHash);
        var service = new UserService(db);
        for (int i = 0; i < 5; i++) Assert.Null(await service.AuthenticateAsync("migration-user", "wrong"));
        await migrator.MigrateAsync();
        Assert.Null(await service.AuthenticateAsync("migration-user", Password));
        Assert.Equal(5, (await db.CredentialAttempts.SingleAsync()).Failures);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task AuthorizedAccountLifecycle_PreservesSecretsOnProfileEditRotatesCredentialsAndDeactivatesOnce()
    {
        await using var db = Db();
        var audit = new SpyAuditService();
        var users = new UserService(db, audit, operations: _actor, quickAccess: _quickAccess);
        string id = Guid.NewGuid().ToString();
        await users.SaveAsync(id, "Synthetic cashier", " cashier ", "CAJERO", Password, "1234", "never persist");
        var created = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id));
        Assert.Equal("cashier", created.Username);
        Assert.Empty(created.PasswordHint);
        Assert.True(SecurityManager.VerifyPassword(Password, created.PasswordHash));
        Assert.True(SecurityManager.VerifyPin("1234", created.PinCode));
        Assert.Null(await users.GetPasswordHintAsync("cashier"));
        await users.SaveAsync(id, "Renamed", "cashier", "CAJERO", null, null);
        var renamed = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id));
        Assert.Equal(created.PasswordHash, renamed.PasswordHash);
        Assert.Equal(created.PinCode, renamed.PinCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "Duplicate", "CASHIER", "CAJERO", Password, "1234"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "", "invalid", "CAJERO", Password, "1234"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "No password", "nopassword", "CAJERO", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.ChangePasswordAsync(id, ""));
        await users.ChangePasswordAsync(id, "Rotated-test-password");
        await users.ChangePinAsync(id, "9876");
        Assert.NotNull(await users.AuthenticateAsync("cashier", "Rotated-test-password"));
        Assert.True(await users.VerifyCredentialAsync(id, "9876", true));
        await users.ChangePinAsync(id, "");
        Assert.False(await users.VerifyCredentialAsync(id, "9876", true));
        await users.DeleteAsync(id);
        await users.DeleteAsync(id);
        Assert.Null(await users.AuthenticateAsync("cashier", "Rotated-test-password"));
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EntityId == id && j.EventType == "USER_INACTIVATED"));
        Assert.Equal(3, audit.CapturedLogs.Count);
        Assert.All(audit.CapturedLogs, log => Assert.Equal(CommercialTestSession.UserId.ToString(), log.UserId));
        await users.ChangePasswordAsync("invalid", "ignored");
        await users.ChangePinAsync("invalid", "9876");
        await users.ChangePasswordAsync(Guid.NewGuid().ToString(), "ignored");
        await users.ChangePinAsync(Guid.NewGuid().ToString(), "9876");
    }

    [Fact]
    public async Task AdministratorMayDemoteAnotherAdminButNotTheLastAndCashierCannotRotateCredentials()
    {
        await using var db = Db();
        var users = new UserService(db, operations: _actor);
        var other = Guid.NewGuid();
        await users.SaveAsync(other.ToString(), "Second admin", "second", "ADMIN", Password, "1234");
        Assert.True(await users.VerifyManagerCredentialAsync(other.ToString(), Password));
        await users.SaveAsync(other.ToString(), "Manager", "second", "SUPERVISOR", null, null);
        Assert.True(await users.VerifyManagerCredentialAsync(other.ToString(), Password));
        var cashier = Guid.NewGuid();
        await users.SaveAsync(cashier.ToString(), "Cashier", "cashier", "", Password, null);
        var actor = CommercialTestSession.Create(db, openShift: false, userId: cashier);
        var restricted = new UserService(db, operations: actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.ChangePasswordAsync(other.ToString(), Password));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.ChangePinAsync(other.ToString(), "0000"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.ConfirmCurrentAdministratorAsync(Password));
        Assert.False(await users.VerifyManagerCredentialAsync(cashier.ToString(), Password));
    }

    [Fact]
    public async Task InitialAccountMustBeAdministratorAndProtected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var service = new UserService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync("new", "First", "first", "CAJERO", Password, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync("new", "First", "first", "ADMIN", null, null));
        Assert.Null(await new UserRepository(db).GetAdminProfileAsync());
        await service.SaveAsync("new", "First", "first", "ADMIN", Password, "1234");
        Assert.NotNull(await service.AuthenticateAsync("first", Password));
        Assert.Equal("bootstrap:local", (await db.TransactionJournal.SingleAsync()).RegisterId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync("another", "Other", "other", "ADMIN", Password, "1234"));
    }

    [Fact]
    public async Task UserManagementWithoutRoleManagement_CannotTakeOverPrivilegedAccounts()
    {
        await using var db = Db();
        var actor = await LimitedAccountManagerAsync(db);
        var users = new UserService(db, operations: actor);
        string adminId = CommercialTestSession.UserId.ToString();
        string hash = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == CommercialTestSession.UserId)).PasswordHash;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.ChangePasswordAsync(adminId, "takeover"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.ChangePinAsync(adminId, "0000"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.DeleteAsync(adminId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.SaveAsync(adminId, "Changed", "administrator", "ADMIN", "takeover", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "Custom", "custom", "ACCOUNT_OWNER", Password, "1234"));
        var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Id == CommercialTestSession.UserId);
        Assert.Equal(hash, admin.PasswordHash);
        Assert.True(admin.IsActive);
        Assert.Empty(await db.TransactionJournal.ToListAsync());
        string cashierId = Guid.NewGuid().ToString();
        await users.SaveAsync(cashierId, "Cashier", "basic", "CAJERO", Password, "1234");
        await users.ChangePasswordAsync(cashierId, "Allowed-test-change");
        await users.ChangePinAsync(cashierId, "9876");
        await users.DeleteAsync(cashierId);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(cashierId))).IsActive);
    }

    [Fact]
    public async Task Repository_AuthorizesEffectiveRoleAndDoesNotOverwriteAnUnrelatedAdministrator()
    {
        await using var db = Db();
        var actor = await LimitedAccountManagerAsync(db);
        var repository = new UserRepository(db, operations: actor);
        foreach (string role in new[] { "ADMINISTRADOR", "GERENTE", "ACCOUNT_OWNER" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.CreateUserAsync(new UserAccountDraft
            { Username = "forbidden", FullName = "Synthetic", Role = role, Password = Password }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        { Username = "replacement", FullName = "Synthetic", Role = "CAJERO", Password = Password }));
        Assert.Single(await db.Users.Where(u => u.Username == "administrator").ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());

        var authorized = new UserRepository(db, operations: _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authorized.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        { Username = "unrelated", FullName = "Synthetic", Role = "ADMIN", Password = Password }));
        var update = new UserAccountDraft
        {
            Id = CommercialTestSession.UserId,
            Username = " renamed-admin ",
            FullName = "Renamed",
            Role = "ADMIN",
            Password = "Updated-test-secret",
            Pin = "9876"
        };
        await authorized.SaveOrUpdateAdminUserAsync(update);
        var saved = await db.Users.AsNoTracking().SingleAsync(u => u.Id == update.Id);
        Assert.Equal("renamed-admin", saved.Username);
        Assert.Equal(UserRole.Admin, saved.Role);
        Assert.Equal("ADMINISTRADOR", saved.RoleString);
        Assert.True(SecurityManager.VerifyPassword("Updated-test-secret", saved.PasswordHash));
        Assert.True(SecurityManager.VerifyPin("9876", saved.PinCode));
        Assert.Equal(CommercialTestSession.UserId.ToString(), (await db.TransactionJournal.SingleAsync()).CashierUserId);
    }

    [Fact]
    public async Task RepositoryBootstrapAndLifecycle_ValidateAccountsAndRecordRealActor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var audit = new SpyAuditService();
        var repository = new UserRepository(db, auditService: audit, auditActor: () => "spoofed", quickAccess: _quickAccess);
        Assert.False(await repository.HasAnyUsersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = "invalid", FullName = "Synthetic", Role = "CAJERO", Password = Password }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = "invalid", FullName = "Synthetic", Role = "ADMINISTRADOR", Password = Password, IsActive = false }));
        var admin = await repository.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        { Username = " owner ", FullName = "Owner", Role = "ADMIN", Password = Password, Pin = "4321" });
        Assert.True(await repository.HasAnyUsersAsync());
        Assert.Equal(Ticketfy.Core.Models.SystemRole.ADMIN, admin.Role);
        Assert.Empty(admin.Pin4Digits);
        Assert.Single(await repository.GetActiveUsersAsync());
        Assert.Null(await repository.ValidatePinAsync("owner", "4321"));
        Assert.NotNull(await repository.VerifyPasswordAsync("owner", Password));
        Assert.Equal(admin.Id, (await repository.ValidatePinAsync("owner", "4321"))!.Id);
        var actor = CommercialTestSession.Create(db, openShift: false, userId: admin.Id);
        repository = new UserRepository(db, auditService: audit, auditActor: () => "spoofed", operations: actor);
        var cashier = await repository.CreateUserAsync(new UserAccountDraft { Username = "cashier", FullName = "Cashier", Password = Password, Pin = "1234" });
        Assert.Equal("CAJERO", cashier.RoleString);
        var storedCashier = await db.Users.AsNoTracking().SingleAsync(u => u.Id == cashier.Id);
        Assert.True(SecurityManager.VerifyPassword(Password, storedCashier.PasswordHash));
        Assert.True(SecurityManager.VerifyPin("1234", storedCashier.PinCode));
        Assert.Empty(cashier.Pin4Digits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = " CASHIER ", FullName = "Duplicate", Password = Password }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = "empty-password", FullName = "Invalid" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = "", FullName = "Invalid", Password = Password }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Username = "numeric-role", FullName = "Invalid", Password = Password, Role = "0" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        { Id = cashier.Id, Username = cashier.Username, FullName = "Invalid", Role = "ADMIN", Password = Password }));
        await repository.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        {
            Id = admin.Id,
            Username = admin.Username,
            FullName = admin.FullName,
            Role = "ADMINISTRADOR",
            Password = "Changed-synthetic-secret",
            Pin = "4321"
        });
        Assert.Equal(3, audit.CapturedLogs.Count);
        Assert.All(audit.CapturedLogs, log => Assert.Equal(admin.Id.ToString(), log.UserId));
        Assert.Equal(2, await db.Users.CountAsync());
    }

    private static async Task<CommercialOperationContext> LimitedAccountManagerAsync(AppDbContext db)
    {
        var user = new UserEntity { Username = "operator", FullName = "Synthetic", Role = UserRole.Gerente, RoleString = "GERENTE" };
        db.Users.Add(user);
        db.Users.Add(new UserEntity { Username = "spare-admin", FullName = "Synthetic", Role = UserRole.Admin, RoleString = "ADMINISTRADOR" });
        db.Settings.Add(new SettingEntity { Key = "RolePermissions_GERENTE", Value = "{\"users.manage\":true,\"users.roles\":false}" });
        await db.SaveChangesAsync();
        var permissions = new UserRolePermissionEngine(new SettingsService(db));
        var session = new SessionManager();
        session.StartSession(permissions.MapToModel(user));
        return new CommercialOperationContext(session, permissions, "TEST-REGISTER");
    }

    private sealed class CredentialClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    public void Dispose() => _connection.Dispose();
}
