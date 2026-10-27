using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CheckoutFailure_IndexesUpgradeRepeatDowngradeAndPreservePendingAndCommittedDocuments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261020123848_IndexPredictiveReadPaths";
        await db.Database.MigrateAsync();
        var actor = CommercialTestSession.Create(db);
        db.Products.Add(new ProductEntity { Id = "P", Name = "Producto", Barcode = "SYNTHETIC-CHECKOUT-INDEX", Price = 10m, Cost = 3m, Stock = 20 });
        await db.SaveChangesAsync();
        var sales = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), actor);
        await sales.SaveAsync(Sale("INDEX-SALE"));
        await sales.BeginCheckoutAsync(Sale("INDEX-PENDING"));
        string payload = (await db.CheckoutOperations.AsNoTracking().SingleAsync()).SalePayload;
        // Seed through the current contracts, then place the unchanged data at the older schema.
        await migrator.MigrateAsync(previous);
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var query in new[]
        {
            (Sql: CheckoutStatePolicy.Pending(db).Where(o => o.ShiftId == "SHIFT-1").ToQueryString(), Index: "IX_checkout_operations_ShiftId_state"),
            (Sql: CheckoutStatePolicy.Pending(db).Where(o => o.RegisterId == "TEST-REGISTER").ToQueryString(), Index: "IX_checkout_operations_RegisterId_state_created_at_utc_operation_id")
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
            await using var reader = await command.ExecuteReaderAsync();
            var details = new List<string>();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            Assert.Contains(details, value => value.Contains(query.Index, StringComparison.Ordinal));
        }
        await migrator.MigrateAsync(previous); await db.Database.MigrateAsync();
        var pending = await db.CheckoutOperations.AsNoTracking().SingleAsync();
        Assert.Equal("INDEX-PENDING", pending.OperationId); Assert.Equal(payload, pending.SalePayload);
        Assert.Equal(CheckoutOperationState.Started, pending.State);
        Assert.Equal(20m, (await db.Sales.AsNoTracking().SingleAsync()).Total);
        Assert.Equal(20m, (await db.SalePayments.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(18d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
        Assert.Single(await db.InventoryMovements.ToListAsync());
    }
}
