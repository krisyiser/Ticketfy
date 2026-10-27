using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Constants;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Audit;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Settings;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseRecoveryBundleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ticketfy-portable-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _master = Guid.NewGuid().ToString("N");
    private readonly string _integrity = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private const string Passphrase = "Synthetic recovery phrase used only by the automated test";
    private string Database => Path.Combine(_root, "source.db");
    private string AuditDatabase => Path.Combine(_root, "audit.db");
    private string Bundle => Path.Combine(_root, "portable.tfyrecovery");
    private DbContextOptions<AppDbContext> _options = null!;
    private CommercialOperationContext _actor = null!;
    private RecoveryBundleService Service => new(new TestDbContextFactory(_options), _actor, Database, AuditDatabase, () => _master, () => _integrity);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Database, Password = _master, Pooling = false }.ToString()).Options;
        await using (var db = new AppDbContext(_options))
        {
            await db.Database.MigrateAsync();
            _actor = CommercialTestSession.Create(db, openShift: false);
            db.Products.Add(new ProductEntity { Id = "RECOVERY-P", Name = "Synthetic private product", Barcode = "RECOVERY", Price = 19.99m, Cost = 12.34m });
            var settings = new Ticketfy.Core.Models.Settings.AppSettings();
            settings.System.BackupPath = Path.Combine(_root, "source-only-backups");
            db.Settings.Add(new SettingEntity { Key = "SystemGlobalAppSettings_V4", Value = System.Text.Json.JsonSerializer.Serialize(settings) });
            await db.SaveChangesAsync();
        }
        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={AuditDatabase};Pooling=False").Options;
        await using (var db = new AuditDbContext(auditOptions)) await db.Database.MigrateAsync();
        var audit = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(auditOptions), _integrity);
        await audit.LogAsync(new AuditLogEntity { UserId = CommercialTestSession.UserId.ToString(), EntityId = "RECOVERY-P", EntityName = "Product", Reason = "Synthetic audit history" });
        await audit.InitializeIntegrityAsync();
    }
    public Task DisposeAsync() { Directory.Delete(_root, recursive: true); return Task.CompletedTask; }

    [AvaloniaFact]
    public async Task PortableBundle_SettingsAndRecoveryWizardRequireExplicitConfirmationAndClearSecrets()
    {
        var settings = new SeguridadSettingsViewModel(recovery: Service)
        { RecoveryPath = Bundle, RecoveryPassphrase = Passphrase, RecoveryPassphraseConfirmation = Passphrase };
        await settings.CreateRecoveryBundleCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Bundle));
        settings.RecoveryCustodyConfirmed = true;
        await settings.CreateRecoveryBundleCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Bundle));
        Assert.Empty(settings.RecoveryPassphrase);
        Assert.Empty(settings.RecoveryPassphraseConfirmation);
        var model = new RecoveryViewModel { BundlePath = Bundle, NewDataDirectory = Path.Combine(_root, "wizard-copy"), Passphrase = Passphrase };
        var window = new RecoveryWindow { DataContext = model };
        window.Show();
        try
        {
            await model.RestoreCommand.ExecuteAsync(null);
            Assert.False(Directory.Exists(model.NewDataDirectory));
            model.Passphrase = Passphrase; model.DestinationConfirmed = true;
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, model.RestoreCommand));
            button.Command!.Execute(null);
            Task execution = model.RestoreCommand.ExecutionTask!;
            button.Command.Execute(null);
            await execution;
            Assert.True(Directory.Exists(model.NewDataDirectory));
            Assert.Contains("Recuperación verificada", model.Message);
            Assert.Empty(model.Passphrase); Assert.False(model.DestinationConfirmed);
            model.Passphrase = Passphrase;
            model.NewDataDirectory = Path.Combine(_root, "different-copy");
            Assert.False(model.DestinationConfirmed);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PortableBundle_VerifiesAndRestoresBothDatabasesWithReprotectedKeysWithoutSourceProfileFiles()
    {
        await Service.CreateAsync(Bundle, Passphrase);
        string content = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Bundle));
        Assert.DoesNotContain(_master, content); Assert.DoesNotContain(_integrity, content);
        Assert.DoesNotContain("Synthetic private product", content);
        Assert.DoesNotContain("Synthetic audit history", content);
        var check = await RecoveryBundleService.VerifyAsync(Bundle, Passphrase, _root);
        Assert.Empty(check.Directory);
        string destination = Path.Combine(_root, "new-profile-copy");
        var restored = await RecoveryBundleService.RestoreToNewDirectoryAsync(Bundle, Passphrase, destination);
        Assert.Equal(destination, restored.Directory);
        string restoredKey = new DpapiSecretStore(Path.Combine(destination, "Secrets", "database-key.dpapi")).Read();
        string restoredAuditKey = new DpapiSecretStore(Path.Combine(destination, "Secrets", "audit-integrity-key.dpapi")).Read();
        Assert.Equal(_master, restoredKey); Assert.Equal(_integrity, restoredAuditKey);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(destination, "Database", AppConstants.DatabaseFileName), Password = restoredKey, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options;
        await using (var db = new AppDbContext(options))
        {
            Assert.Equal(19.99m, (await db.Products.SingleAsync()).Price);
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(Path.Combine(destination, "Backups"), (await new SettingsService(db).GetAppSettingsAsync()).System.BackupPath);
        }
        string auditPath = Path.Combine(destination, "Database", AppConstants.AuditDatabaseFileName);
        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite($"Data Source={auditPath};Mode=ReadOnly;Pooling=False").Options;
        Assert.True(await new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(auditOptions), restoredAuditKey).VerifyIntegrityAsync());
        Assert.False(Directory.Exists(Path.Combine(destination, "Secrets", "Credentials")));
        Assert.False(File.Exists(Path.Combine(destination, "license.jwt")));
        Assert.Empty(Directory.GetDirectories(_root, ".ticketfy-recovery-*"));
        Assert.True(File.Exists(Database)); // source installation preserved
    }

    [Fact]
    public async Task PortableBundle_RejectsWrongPhraseTamperingAndExistingDestinationBeforePublication()
    {
        await Service.CreateAsync(Bundle, Passphrase);
        string destination = Path.Combine(_root, "not-published");
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoveryBundleService.RestoreToNewDirectoryAsync(Bundle, "Another long but incorrect phrase", destination));
        Assert.False(Directory.Exists(destination));
        byte[] changed = await File.ReadAllBytesAsync(Bundle); changed[^1] ^= 1;
        string tampered = Path.Combine(_root, "tampered.tfyrecovery");
        await File.WriteAllBytesAsync(tampered, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoveryBundleService.RestoreToNewDirectoryAsync(tampered, Passphrase, destination));
        Assert.False(Directory.Exists(destination));
        Directory.CreateDirectory(destination);
        string sentinel = Path.Combine(destination, "untouched.txt");
        await File.WriteAllTextAsync(sentinel, "Do not overwrite");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryBundleService.RestoreToNewDirectoryAsync(Bundle, Passphrase, destination));
        Assert.Equal("Do not overwrite", await File.ReadAllTextAsync(sentinel));
        await Assert.ThrowsAsync<IOException>(() => Service.CreateAsync(Bundle, Passphrase));
        Assert.Empty(Directory.GetDirectories(_root, ".ticketfy-recovery-*"));
    }

    [Fact]
    public async Task PortableBundle_RequiresSecurityPermissionClosedShiftsAndStrongPhrase()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(Bundle, "short"));
        await using var db = new AppDbContext(_options);
        var user = await db.Users.SingleAsync();
        user.RoleString = "CAJERO"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.CreateAsync(Bundle, Passphrase));
        user.RoleString = "ADMINISTRADOR"; await db.SaveChangesAsync();
        CommercialTestSession.Create(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAsync(Bundle, Passphrase));
        Assert.False(File.Exists(Bundle));
    }

    [Fact]
    public async Task PortableBundle_RefusesAnAuditChainWithTheWrongIntegrityKey()
    {
        var wrong = new RecoveryBundleService(new TestDbContextFactory(_options), _actor, Database, AuditDatabase, () => _master,
            () => Convert.ToBase64String(new byte[32]));
        await Assert.ThrowsAsync<InvalidDataException>(() => wrong.CreateAsync(Bundle, Passphrase));
        Assert.False(File.Exists(Bundle));
        Assert.Empty(Directory.GetDirectories(_root, ".ticketfy-recovery-*"));
    }
}
