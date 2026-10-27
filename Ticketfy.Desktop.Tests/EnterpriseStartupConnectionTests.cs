using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Startup;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseStartupConnectionTests
{
    [Fact]
    public void ConnectionPolicy_EscapesPathsAndSecretsAndUsesPrivateCache()
    {
        var connection = new SqliteConnectionStringBuilder(CommercialDatabaseConnection.Create("C:\\test;synthetic\\ticketfy.db", "synthetic;key=value"));
        Assert.Equal("C:\\test;synthetic\\ticketfy.db", connection.DataSource);
        Assert.Equal("synthetic;key=value", connection.Password);
        Assert.Equal(SqliteCacheMode.Private, connection.Cache);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, connection.Mode);
    }

    [Fact]
    public void StartupVerification_RejectsAnExistingDirectoryBeforeSelectingOrOpeningIt()
    {
        var previous = AppDataPaths.RootDirectory;
        Assert.Throws<ArgumentException>(() => StartupVerification.Run([]));
        Assert.Throws<ArgumentException>(() => StartupVerification.Run(["--verify-startup", "--data-directory", "."]));
        Assert.Throws<ArgumentException>(() => StartupVerification.Run(["--verify-startup", "--data-directory", Path.GetTempPath()]));
        Assert.Equal(previous, AppDataPaths.RootDirectory);
    }

    [Fact]
    public async Task EncryptedWalConnections_SurviveRepeatedIndependentOpenReadCloseAndBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "ticketfy-private-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "synthetic.db"), key = Guid.NewGuid().ToString("N");
        string connectionString = CommercialDatabaseConnection.Create(path, key);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                db.Products.Add(new ProductEntity { Id = "P", Barcode = "SYNTHETIC", Name = "Prueba cifrada", Price = 12.34m });
                await db.SaveChangesAsync();
            }
            await using (var keeper = new AppDbContext(options))
            {
                await keeper.Database.OpenConnectionAsync();
                for (int i = 0; i < 8; i++)
                {
                    await using (var other = new AppDbContext(options))
                    {
                        Assert.Equal(12.34m, (await other.Products.SingleAsync()).Price);
                        other.Settings.Add(new() { Key = "SYNTHETIC-" + i, Value = "persisted" });
                        await other.SaveChangesAsync();
                    }
                    Assert.Equal(i + 1, await keeper.Settings.CountAsync());
                    Assert.Equal(12.34m, (await keeper.Products.SingleAsync()).Price);
                }
                var backup = new Ticketfy.Services.Implementations.BackupService(path, Path.Combine(root, "backup"), passwordProvider: () => key);
                Assert.True((await backup.TestRestoreAsync(await backup.CreateBackupAsync())).IsValid);
                Assert.Equal(12.34m, (await keeper.Products.SingleAsync()).Price);
            }
        }
        finally
        {
            using var pool = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, true);
        }
    }
}
