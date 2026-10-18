using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Data.Seed;
using Ticketfy.Services.Security;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Audit;

namespace Ticketfy.Core.Startup;

/// <summary>
/// Applies the versioned database history. A failure is fatal: the application
/// must never run against a partially upgraded transactional schema.
/// </summary>
public static class DatabaseInitializer
{
    private static readonly string[] LegacyMigrationIds =
    [
        "20260801054956_InitialCreate",
        "20260808183950_AddSecurityFields",
        "20260817060006_AddSatProductCodes"
    ];

    public static async Task InitializeAsync()
    {
        try
        {
            AppDataPaths.EnsureRequiredDirectories();
            string securePassword = SecurityManager.GetMasterKey();
            string connectionString = $"Data Source={AppDataPaths.DatabaseFile};Password={securePassword};Cache=Shared;Mode=ReadWriteCreate;";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;

            await AdoptValidatedLegacyHistoryAsync(connectionString);

            await using (var context = new AppDbContext(options))
            {
                await context.Database.MigrateAsync();
                await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                await SeedCategoriesAsync(context);
#if DEBUG
                await DatabaseSeeder.SeedAsync(context);
#endif
                await NormalizeLegacyDatesAsync(context);
                await MigrateLegacySecretsAsync(context);
            }

            string auditConnectionString = $"Data Source={AppDataPaths.AuditDatabaseFile};";
            await AdoptValidatedLegacyAuditHistoryAsync(auditConnectionString);
            var auditOptions = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlite(auditConnectionString)
                .Options;
            await using (var auditContext = new AuditDbContext(auditOptions))
            {
                await auditContext.Database.MigrateAsync();
            }

            var appFactory = new AppDbContextFactoryImpl<AppDbContext>(options);
            var auditFactory = new AppDbContextFactoryImpl<AuditDbContext>(auditOptions);
            var auditService = new AuditService(auditFactory);
            await auditService.InitializeIntegrityAsync();
            var recoveryService = new SaleService(appFactory, new CoOccurrenceQueue(), auditService);
            int recovered = await recoveryService.RecoverInterruptedCheckoutsAsync();
            if (recovered > 0)
                Log.Warning("Recovered {RecoveredCount} payment-authorized checkout operations after interrupted shutdown", recovered);

            Log.Information("Versioned database migrations completed at {DatabasePath}", AppDataPaths.DatabaseFile);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Database migration failed; startup has been stopped");
            throw;
        }
    }

    /// <summary>
    /// Older commercial builds used EnsureCreated plus startup patches and have no
    /// EF history. Adopt them only after proving the complete supported baseline.
    /// This bootstrap writes migration metadata, never business schema.
    /// </summary>
    internal static async Task AdoptValidatedLegacyHistoryAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "products") || await TableExistsAsync(connection, "__EFMigrationsHistory"))
            return;

        var requirements = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["products"] = ["id", "cost", "price", "is_bulk", "sat_product_code", "sat_unit_code"],
            ["sales"] = ["id", "total", "totalCost", "paidAmount", "cashAmount", "cardAmount", "status", "invoice_id"],
            ["usuarios"] = ["id", "role_string", "password_hash_bytes", "password_salt"],
            ["cashups"] = ["id", "type", "cashier_name", "cashier_role"],
            ["categories"] = ["id", "name"],
            ["InventorySnapshots"] = ["Id"],
            ["returns"] = ["id", "total_refunded", "cogs_reversed"],
            ["customer_payments"] = ["id", "amount"]
        };

        foreach (var requirement in requirements)
        {
            if (!await TableExistsAsync(connection, requirement.Key))
                throw new InvalidOperationException($"La base comercial no contiene la tabla requerida '{requirement.Key}'. Se canceló la actualización.");

            var columns = await GetColumnsAsync(connection, requirement.Key);
            var missing = requirement.Value.Where(column => !columns.Contains(column)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"La base comercial no coincide con el esquema soportado. Tabla '{requirement.Key}', columnas faltantes: {string.Join(", ", missing)}.");
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = (SqliteTransaction)transaction;
            create.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        foreach (string migrationId in LegacyMigrationIds)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES ($id, '9.0.2');";
            insert.Parameters.AddWithValue("$id", migrationId);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        Log.Information("Adopted validated legacy commercial schema into versioned migration history");
    }

    internal static async Task AdoptValidatedLegacyAuditHistoryAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        if (!await TableExistsAsync(connection, "audit_logs") || await TableExistsAsync(connection, "__EFMigrationsHistory")) return;

        var columns = await GetColumnsAsync(connection, "audit_logs");
        string[] baseline = ["id", "timestamp", "user_id", "action_type", "risk_level", "entity_name", "entity_id", "old_value", "new_value", "financial_impact", "reason", "terminal_name"];
        var missing = baseline.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"La auditoría heredada no coincide con el esquema soportado. Columnas faltantes: {string.Join(", ", missing)}.");

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = (SqliteTransaction)transaction;
            create.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES ('20261018111534_InitialAuditSchema', '9.0.2');";
            await insert.ExecuteNonQueryAsync();
        }
        if (columns.Contains("entry_hash") && columns.Contains("previous_hash") && columns.Contains("schema_version"))
        {
            await using var insertCurrent = connection.CreateCommand();
            insertCurrent.Transaction = (SqliteTransaction)transaction;
            insertCurrent.CommandText = "INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES ('20261018190000_AddAuditIntegrityChain', '9.0.2');";
            await insertCurrent.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        Log.Information("Adopted validated legacy audit schema into versioned migration history");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(1));
        return result;
    }

    private static async Task SeedCategoriesAsync(AppDbContext context)
    {
        if (await context.Categories.AnyAsync()) return;
        context.Categories.AddRange(new[] { "General", "Abarrotes", "Bebidas", "Farmacia", "Hogar" }
            .Select(name => new CategoryEntity { Id = Guid.NewGuid().ToString(), Name = name }));
        await context.SaveChangesAsync();
    }

    private static async Task NormalizeLegacyDatesAsync(AppDbContext context)
    {
        var legacySales = await context.Sales.Where(s => !s.Date.Contains("T")).ToListAsync();
        foreach (var sale in legacySales)
        {
            if (DateTime.TryParse(sale.Date, out var parsedDate))
                sale.Date = new DateTimeOffset(DateTime.SpecifyKind(parsedDate, DateTimeKind.Local)).UtcDateTime.ToString("O");
        }
        if (legacySales.Count > 0) await context.SaveChangesAsync();
    }

    private static async Task MigrateLegacySecretsAsync(AppDbContext context)
    {
        string[] credentialKeys = ["FacturamaApiUser", "FacturamaApiPassword", "PaymentApiToken"];
        var legacyCredentials = await context.Settings.Where(s => credentialKeys.Contains(s.Key) && s.Value != string.Empty).ToListAsync();
        var credentialStore = new SecureCredentialStore();
        foreach (var setting in legacyCredentials)
        {
            credentialStore.Write(setting.Key, setting.Value);
            setting.Value = string.Empty;
        }

        var usersWithHints = await context.Users.Where(u => u.PasswordHint != string.Empty).ToListAsync();
        foreach (var user in usersWithHints) user.PasswordHint = string.Empty;
        if (legacyCredentials.Count > 0 || usersWithHints.Count > 0) await context.SaveChangesAsync();
    }
}
