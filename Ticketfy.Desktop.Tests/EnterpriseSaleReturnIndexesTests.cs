using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task SaleReturnIndexes_UpgradeRepeatDowngradePreservesLedgersAndUsesIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var originalOptions = _options; var originalActor = _actor;
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = Db();
            var migrator = db.GetService<IMigrator>();
            const string previous = "20261020163149_IndexRefundRecoveryQueues";
            await migrator.MigrateAsync(previous);
            _actor = CommercialTestSession.Create(db);
            db.Products.Add(new() { Id = "P", Barcode = "P", Name = "Producto", Price = 10m, Cost = 3m, Stock = 20, Unit = "Pza" });
            db.Giftcards.Add(new() { Id = "G", CardNumber = "WALLET", Balance = 100m, IsActive = true });
            db.Customers.Add(new() { Id = "C", Name = "Cliente", CreditLimit = 1000m });
            await db.SaveChangesAsync();
            var sale = await Sales.SaveAsync(Sale("RETURN-INDEX") with
            { CashAmount = 10m, GiftcardAmount = 10m, GiftcardNumber = "WALLET", PaymentMethod = "Mixto" });
            await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Primera", operationId: "INDEX-PART");
            var before = await ReturnStateAsync();
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(before, await ReturnStateAsync());
            foreach (var (sql, index) in new[]
            {
                ("SELECT Id FROM payment_refunds WHERE ReturnId = 'R'", "IX_payment_refunds_ReturnId"),
                ("SELECT Id FROM inventory_movements WHERE DocumentId = 'D' AND Reason = 'SALE'", "IX_inventory_movements_DocumentId_Reason"),
                ("SELECT Id FROM inventory_movements WHERE SaleLineId = 'L' AND Reason = 'RETURN'", "IX_inventory_movements_SaleLineId_Reason"),
                ("SELECT Amount FROM account_entries WHERE DocumentId = 'D' AND EntryType = 'SALE_REWARD'", "IX_account_entries_DocumentId_EntryType")
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                await using var reader = await command.ExecuteReaderAsync();
                var details = new List<string>();
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));
                Assert.Contains(details, detail => detail.Contains(index, StringComparison.Ordinal));
            }
            await migrator.MigrateAsync(previous);
            Assert.Equal(before, await ReturnStateAsync());
            await db.Database.MigrateAsync();
            Assert.Equal(before, await ReturnStateAsync());
            Assert.True(await Sales.CancelSaleAsync(sale.Id, "Resto"));
            var after = await ReturnStateAsync();
            Assert.False(await Sales.CancelSaleAsync(sale.Id, "Resto"));
            Assert.Equal(after, await ReturnStateAsync());
            Assert.Equal(20d, after.Stock); Assert.Equal(0m, after.Cash); Assert.Equal(100m, after.Wallet);
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM payment_refunds;"));
        }
        finally { _options = originalOptions; _actor = originalActor; }
    }
}
