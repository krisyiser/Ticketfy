using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Security;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.Services.Audit;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Startup;
using System.Security.Cryptography;
using Ticketfy.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage2SecurityTests
{
    [Fact]
    public async Task UserRepository_ShouldUpgradeLegacyPlaintextPinAndNeverExposeIt()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var entity = new UserEntity { Id = Guid.NewGuid(), Username = "cashier", FullName = "Cashier", PinCode = "1234", IsActive = true };
        context.Users.Add(entity);
        await context.SaveChangesAsync();

        var repository = new UserRepository(context);
        var result = await repository.ValidatePinAsync("cashier", "1234");

        Assert.NotNull(result);
        Assert.Empty(result.Pin4Digits);
        Assert.StartsWith("$pbkdf2$sha256$600000$", entity.PinCode);
        Assert.True(SecurityManager.VerifyPin("1234", entity.PinCode));
    }

    [Fact]
    public async Task UserRepository_ShouldLockPinAfterRepeatedFailures()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        string username = $"locked-{Guid.NewGuid():N}";
        context.Users.Add(new UserEntity { Id = Guid.NewGuid(), Username = username, FullName = "Locked", PinCode = "4321", IsActive = true });
        await context.SaveChangesAsync();
        var repository = new UserRepository(context);

        for (int i = 0; i < 5; i++) Assert.Null(await repository.ValidatePinAsync(username, "0000"));
        Assert.Null(await repository.ValidatePinAsync(username, "4321"));
    }

    [Fact]
    public async Task Rbac_ShouldDenyUnknownKeysAndUnknownRoles()
    {
        var engine = new UserRolePermissionEngine();

        Assert.True(await engine.HasPermissionAsync("ADMINISTRADOR", PermissionCatalog.SettingsSecurity));
        Assert.False(await engine.HasPermissionAsync("GERENTE", PermissionCatalog.UsersRoles));
        Assert.False(await engine.HasPermissionAsync("CUSTOM", PermissionCatalog.PosCheckout));
        Assert.False(await engine.HasPermissionAsync("ADMINISTRADOR", "unregistered.permission"));
    }

    [Fact]
    public async Task Rbac_ShouldHonorStoredOverrideForKnownPermissionOnly()
    {
        var settings = new InMemorySettingsService();
        await settings.SetAsync("RolePermissions_CAJERO", "{\"pos.discount\":true,\"unknown\":true}");
        var engine = new UserRolePermissionEngine(settings);

        Assert.True(await engine.HasPermissionAsync("CAJERO", PermissionCatalog.PosDiscount));
        Assert.False(await engine.HasPermissionAsync("CAJERO", "unknown"));
    }

    [Fact]
    public async Task AuditLog_ShouldBeHashChainedAndAppendOnly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-audit-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var setup = new AuditDbContext(options)) await setup.Database.EnsureCreatedAsync();
            var factory = new AppDbContextFactoryImpl<AuditDbContext>(options);
            var audit = new AuditService(factory, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            await audit.InitializeIntegrityAsync();
            await audit.LogAsync(new AuditLogEntity
            {
                UserId = "admin-id",
                ActionType = AuditActionType.PriceOverride,
                RiskLevel = RiskLevel.Warning,
                EntityName = "Product",
                EntityId = "P-1",
                Reason = "approved"
            });

            Assert.True(await audit.VerifyIntegrityAsync());
            await using var context = new AuditDbContext(options);
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs"));
            await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync("UPDATE audit_logs SET reason='changed'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AuditMigration_ShouldAdoptAndUpgradeLegacyDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-audit-legacy-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path}";
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE audit_logs (
                      id TEXT NOT NULL PRIMARY KEY, timestamp TEXT NOT NULL, user_id TEXT NOT NULL,
                      authorized_by_supervisor_id TEXT NULL, action_type INTEGER NOT NULL, risk_level INTEGER NOT NULL,
                      entity_name TEXT NOT NULL, entity_id TEXT NOT NULL, old_value TEXT NOT NULL, new_value TEXT NOT NULL,
                      financial_impact TEXT NOT NULL, reason TEXT NOT NULL, terminal_name TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await DatabaseInitializer.AdoptValidatedLegacyAuditHistoryAsync(connectionString);
            var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connectionString).Options;
            await using var context = new AuditDbContext(options);
            await context.Database.MigrateAsync();

            var columns = await context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('audit_logs')").ToListAsync();
            Assert.Contains("entry_hash", columns);
            Assert.Contains("previous_hash", columns);
            Assert.Contains("schema_version", columns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Logs_ShouldRedactCredentialsAndPersonalData()
    {
        const string input = "email=persona@example.com RFC=XAXX010101000 token=abc.def.ghi password=hunter2 card=4111 1111 1111 1111";
        string result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("persona@example.com", result);
        Assert.DoesNotContain("XAXX010101000", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("4111 1111 1111 1111", result);
    }

    [Fact]
    public void LicensePolicy_ShouldPersistTrialAndRejectClockRollback()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ticketfy-license-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(2048);
        DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var service = new LicenseEnforcementService(Path.Combine(directory, "license.jwt"), Path.Combine(directory, "state.json"),
            rsa.ExportSubjectPublicKeyInfoPem(), () => now, "DEVICE-A", protectState: false);
        try
        {
            var evaluation = service.Evaluate();
            Assert.Equal(LicenseState.Evaluation, evaluation.State);
            Assert.True(evaluation.IsAllowed);

            now = now.AddDays(10);
            Assert.Equal(20, service.Evaluate().DaysRemaining);
            now = now.AddDays(21);
            Assert.Equal(LicenseState.EvaluationExpired, service.Evaluate().State);
            now = now.AddDays(-32);
            Assert.Equal(LicenseState.ClockRollback, service.Evaluate().State);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LicensePolicy_ShouldRequireSignedDeviceBoundToken()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ticketfy-license-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(2048);
        DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        string token = CreateLicense(rsa, now, "DEVICE-A");
        string licensePath = Path.Combine(directory, "license.jwt");
        File.WriteAllText(licensePath, token);
        try
        {
            var valid = new LicenseEnforcementService(licensePath, Path.Combine(directory, "state-a.json"),
                rsa.ExportSubjectPublicKeyInfoPem(), () => now, "DEVICE-A", false).Evaluate();
            var wrongDevice = new LicenseEnforcementService(licensePath, Path.Combine(directory, "state-b.json"),
                rsa.ExportSubjectPublicKeyInfoPem(), () => now, "DEVICE-B", false).Evaluate();

            Assert.Equal(LicenseState.Licensed, valid.State);
            Assert.True(valid.IsAllowed);
            Assert.Equal(LicenseState.DeviceMismatch, wrongDevice.State);
            Assert.False(wrongDevice.IsAllowed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateLicense(RSA rsa, DateTimeOffset now, string deviceId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "Ticketfy POS",
            Audience = "Ticketfy Client",
            NotBefore = now.UtcDateTime.AddMinutes(-1),
            IssuedAt = now.UtcDateTime,
            Expires = now.UtcDateTime.AddDays(30),
            Subject = new ClaimsIdentity([new Claim("device_id", deviceId), new Claim(JwtRegisteredClaimNames.Sub, "test-customer")]),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task<Dictionary<string, string>> GetAllAsync() => Task.FromResult(new Dictionary<string, string>(_values));
        public Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync() => Task.FromResult(new Ticketfy.Core.Models.Settings.AppSettings());
        public Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings) => Task.CompletedTask;
    }
}
