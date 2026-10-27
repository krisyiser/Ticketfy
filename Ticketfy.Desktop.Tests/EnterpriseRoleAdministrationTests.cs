using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Ticketfy.Core.Security;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Data;
using Ticketfy.Core.Repositories;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(PermissionCatalog.UsersManage)]
    [InlineData(PermissionCatalog.UsersRoles)]
    [InlineData(PermissionCatalog.SettingsBusiness)]
    [InlineData(PermissionCatalog.SettingsSecurity)]
    public async Task RolePolicy_CannotRemoveAdministratorRecoveryAccess(string permission)
    {
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        var value = JsonSerializer.Serialize(new Dictionary<string, bool> { [permission] = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_ADMINISTRADOR", value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_admin", value));
        Assert.Null(await settings.GetAsync("RolePermissions_ADMINISTRADOR"));
        await using var db = Db();
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task RolePolicy_CustomRolesCannotDisappearWhileAssignedAndRemovalClearsPolicyAtomically()
    {
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        await settings.SetAsync("CustomRolesList", "[\" auditor \"]");
        Assert.Equal("[\"AUDITOR\"]", await settings.GetAsync("CustomRolesList"));
        await settings.SetAsync("RolePermissions_auditor", "{\"reports.view\":true}");
        Assert.NotNull(await settings.GetAsync("RolePermissions_AUDITOR"));
        await using var db = Db();
        var user = new UserEntity { Username = "auditor", FullName = "Synthetic", RoleString = "AUDITOR", IsActive = false };
        db.Users.Add(user); await db.SaveChangesAsync();
        int before = await db.TransactionJournal.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("CustomRolesList", "[]"));
        Assert.Equal(before, await db.TransactionJournal.CountAsync());
        Assert.NotNull(await settings.GetAsync("RolePermissions_AUDITOR"));
        user.RoleString = "CAJERO"; await db.SaveChangesAsync();
        await settings.SetAsync("CustomRolesList", "[]");
        Assert.Equal("[]", await settings.GetAsync("CustomRolesList"));
        Assert.Null(await settings.GetAsync("RolePermissions_AUDITOR"));
        Assert.Equal(before + 1, await db.TransactionJournal.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_AUDITOR", "{\"reports.view\":true}"));
    }

    [Theory]
    [InlineData("[\"ADMIN\"]")]
    [InlineData("[\"0\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"A\",\"a\"]")]
    [InlineData("[\"../ROLE\"]")]
    [InlineData("null")]
    public async Task RolePolicy_RejectsInvalidCatalogWithoutSaving(string value)
    {
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("CustomRolesList", value));
        Assert.Null(await settings.GetAsync("CustomRolesList"));
    }

    [Fact]
    public async Task RolePolicy_JournalFailureRollsBackRemovalAndItsPermissions()
    {
        await using var db = Db();
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        await settings.AddCustomRoleAsync("AUDITOR");
        await settings.SetAsync("RolePermissions_AUDITOR", "{\"reports.view\":true}");
        int count = await db.TransactionJournal.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_role_journal BEFORE INSERT ON transaction_journal BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => settings.DeleteCustomRoleAsync("AUDITOR"));
        Assert.Equal("[\"AUDITOR\"]", await settings.GetAsync("CustomRolesList"));
        Assert.NotNull(await settings.GetAsync("RolePermissions_AUDITOR"));
        Assert.Equal(count, await db.TransactionJournal.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_role_journal;");
        await settings.DeleteCustomRoleAsync("AUDITOR");
        await settings.DeleteCustomRoleAsync("AUDITOR");
        Assert.Null(await settings.GetAsync("RolePermissions_AUDITOR"));
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "ROLE_DELETED"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.AddCustomRoleAsync("ADMIN"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.AddCustomRoleAsync(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_", "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_CAJERO", "{\"unknown\":true}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("RolePermissions_CAJERO", new string('x', 65537)));
        await settings.SetAsync("RolePermissions_ADMINISTRADOR", "{\"reports.profits\":false}");
        await settings.SetAsync("RolePermissions_ADMINISTRADOR", "");
        Assert.Equal("{}", await settings.GetAsync("RolePermissions_ADMINISTRADOR"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SettingsService(new TestDbContextFactory(_options)).AddCustomRoleAsync("FORBIDDEN"));
    }

    [Fact]
    public async Task RolePolicy_ConcurrentAdditionsAndDoubleClickDoNotReplaceOtherRoles()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-role-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var setup = new AppDbContext(options);
            await setup.Database.EnsureCreatedAsync();
            var actor = CommercialTestSession.Create(setup, openShift: false);
            await Task.WhenAll(new[] { "AUDITOR", "WAREHOUSE", "SUPPORT", "AUDITOR" }.Select(role => Task.Run(async () =>
            {
                var service = new SettingsService(new TestDbContextFactory(options), actor);
                await service.AddCustomRoleAsync(role);
            })));
            var settings = new SettingsService(new TestDbContextFactory(options), actor);
            Assert.Equal("[\"AUDITOR\",\"SUPPORT\",\"WAREHOUSE\"]", await settings.GetAsync("CustomRolesList"));
            Assert.Equal(3, await setup.TransactionJournal.CountAsync(j => j.EventType == "ROLE_CREATED"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RolePolicy_NewAssignmentsRequireRegisteredRolesWhileLegacyProfilesKeepTheirIdentity()
    {
        await using var db = Db();
        var users = new UserService(db, operations: _actor);
        var settings = new SettingsService(db, _actor);
        const string password = "Synthetic-role-password";
        string id = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(id, "Auditor", "auditor", "AUDITOR", password, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserRepository(db, operations: _actor).CreateUserAsync(
            new UserAccountDraft { Username = "auditor", FullName = "Auditor", Role = "AUDITOR", Password = password }));
        await settings.AddCustomRoleAsync("AUDITOR");
        await users.SaveAsync(id, "Auditor", "auditor", "AUDITOR", password, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.DeleteCustomRoleAsync("AUDITOR"));
        var legacy = new UserEntity { Username = "legacy", FullName = "Legacy", RoleString = "LEGACY_AUDIT" };
        db.Users.Add(legacy); await db.SaveChangesAsync();
        await users.SaveAsync(legacy.Id.ToString(), "Legacy renamed", legacy.Username, legacy.RoleString, password, null);
        Assert.Equal("LEGACY_AUDIT", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).RoleString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync("CustomRolesList",
            JsonSerializer.Serialize(Enumerable.Range(0, 101).Select(i => "ROLE_" + i))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.AddCustomRoleAsync(new string('A', 65)));
    }

    [Fact]
    public async Task RolePolicy_AssignedLegacyUnicodeRoleCannotBeRemoved()
    {
        await using var db = Db();
        var settings = new SettingsService(db, _actor);
        await settings.AddCustomRoleAsync("AUDÍTOR");
        db.Users.Add(new UserEntity { Username = "legacy-auditor", FullName = "Synthetic", RoleString = " audítor ", IsActive = false });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.DeleteCustomRoleAsync("AUDÍTOR"));
        Assert.Contains("AUDÍTOR", JsonSerializer.Deserialize<string[]>((await settings.GetAsync("CustomRolesList"))!)!);
    }
}
