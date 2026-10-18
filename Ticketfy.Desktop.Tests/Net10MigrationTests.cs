using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed class Net10MigrationTests
{
    public static IEnumerable<object[]> MigrationCheckpoints()
    {
        using var context = new AppDbContextFactory().CreateDbContext([]);
        return context.Database.GetMigrations().Select(id => new object[] { id }).ToArray();
    }

    [Theory]
    [MemberData(nameof(MigrationCheckpoints))]
    public async Task EveryRecordedSchema_UpgradesAndCanBeAppliedAgain(string checkpoint)
    {
        // Synthetic historical schemas, not copies of an operator's database.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(checkpoint);
        await migrator.MigrateAsync();
        var first = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        await migrator.MigrateAsync();

        Assert.Equal(context.Database.GetMigrations(), first);
        Assert.Equal(first, await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Products.ToListAsync());
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await integrity.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EncryptedDatabase_ReopensAndBacksUpWithoutRescalingMoney()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ticketfy-net10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "synthetic.db");
        string testKey = Guid.NewGuid().ToString("N");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Password = testKey,
            Pooling = false
        };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(builder.ToString()).Options;
        try
        {
            await using (var original = new AppDbContext(options))
            {
                await original.Database.MigrateAsync();
                original.Products.Add(new ProductEntity
                {
                    Id = "NET10-PRODUCT",
                    Barcode = "NET10",
                    Name = "Prueba cifrada",
                    Cost = 12.34m,
                    Price = 19.99m,
                    Stock = 2.5,
                    Category = "Prueba",
                    Unit = "Kg"
                });
                await original.SaveChangesAsync();
            }

            await using (var reopened = new AppDbContext(options))
            {
                await reopened.Database.MigrateAsync();
                var ids = new[] { "NET10-PRODUCT", "MISSING" };
                var product = await reopened.Products.Where(p => ids.Contains(p.Id)).SingleAsync();
                Assert.Equal(12.34m, product.Cost);
                Assert.Equal(19.99m, product.Price);
                Assert.Equal(2.5, product.Stock);
            }

            var backup = new BackupService(database, Path.Combine(root, "backups"),
                passwordProvider: () => testKey,
                pendingRestorePath: Path.Combine(root, "pending.db"),
                pendingManifestPath: Path.Combine(root, "pending.json"));
            string backupPath = await backup.CreateBackupAsync("net10");
            var restored = await backup.TestRestoreAsync(backupPath);
            Assert.True(restored.IsValid, restored.Message);
            builder.DataSource = backupPath;
            await using (var copy = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(builder.ToString()).Options))
            {
                var product = await copy.Products.SingleAsync();
                Assert.Equal(12.34m, product.Cost);
                Assert.Equal(19.99m, product.Price);
                Assert.Equal(2.5, product.Stock);
                Assert.Equal(copy.Database.GetMigrations(), await copy.Database.GetAppliedMigrationsAsync());
            }

            builder.Password = "";
            await using var withoutKey = new SqliteConnection(builder.ToString());
            await withoutKey.OpenAsync();
            await using var command = withoutKey.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sqlite10_ExplicitOffsetIsUtc_AndLegacyLocalDateKeepsWallClock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT '2026-10-18 12:30:00-06:00', '2026-10-18 12:30:00';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        DateTime explicitOffset = reader.GetDateTime(0);
        Assert.Equal(new DateTime(2026, 10, 18, 18, 30, 0, DateTimeKind.Utc), explicitOffset);
        Assert.Equal(DateTimeKind.Utc, explicitOffset.Kind);
        DateTime legacyLocal = reader.GetDateTime(1);
        Assert.Equal(DateTimeKind.Unspecified, legacyLocal.Kind);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 30, 0), legacyLocal.ToBusinessLocalTime());
        Assert.Equal(TimeSpan.Zero, reader.GetDateTimeOffset(1).Offset);
    }

    [Fact]
    public async Task AuditSchema_StillMatchesAndMigratesIdempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AuditDbContext(
            new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.AuditLogs.ToListAsync());
    }
}
