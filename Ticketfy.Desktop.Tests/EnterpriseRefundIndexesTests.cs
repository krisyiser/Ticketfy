using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task RefundIndexes_UpgradeRepeatDowngradePreservesFundsAndUsesIndexedLookups()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var originalOptions = _options; var originalActor = _actor;
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            await using var db = Db(); await db.Database.MigrateAsync();
            _actor = CommercialTestSession.Create(db);
            db.Products.Add(new() { Id = "P", Barcode = "P", Name = "Producto", Price = 10m, Cost = 3m, Stock = 20, Unit = "Pza" });
            db.Customers.Add(new() { Id = "C", Name = "Cliente", CreditLimit = 1000m });
            await db.SaveChangesAsync();
            string source = await PaidCustomerReturnAsync();
            await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 5m, "Efectivo", "Pendiente", "INDEX-PENDING");
            await Credits.RefundAsync(AccountKind.Customer, "C", source, 5m, "Efectivo", null, "Entregado", "INDEX-PAID", true);
            await SeedRefundQueuesAsync(1, false);
            var migrator = db.GetService<IMigrator>();
            const string previous = "20261020141305_PreserveTerminalEvidence";
            await migrator.MigrateAsync(previous);
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            foreach (var query in new[]
            {
                (Sql: AccountCreditSettlementService.PendingRequests(db).Where(r => r.RegisterId == "TEST-REGISTER").ToQueryString(),
                    Indexes: new[] { "IX_credit_refund_requests_RegisterId_OperationId", "IX_shift_movements_OperationId" }),
                (Sql: db.PaymentRefunds.Where(r => r.State == TenderState.RequiresReconciliation).OrderBy(r => r.Id).ToQueryString(),
                    Indexes: new[] { "IX_payment_refunds_State_Id" })
            })
            {
                await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
                await using var reader = await command.ExecuteReaderAsync();
                var details = new List<string>();
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));
                foreach (string index in query.Indexes) Assert.Contains(details, line => line.Contains(index, StringComparison.Ordinal));
            }
            await migrator.MigrateAsync(previous); await db.Database.MigrateAsync();
            Assert.Equal("INDEX-PENDING", Assert.Single(await Credits.GetPendingRefundsAsync()).OperationId);
            Assert.Equal(5m, (await db.AccountCreditSettlements.AsNoTracking().SingleAsync()).Amount);
            Assert.Equal(-5m, (await db.Customers.AsNoTracking().SingleAsync()).Debt);
            Assert.Equal(15m, (await db.Shifts.AsNoTracking().SingleAsync()).ExpectedBalance);
            Assert.Single(await Reconciliation.GetPendingRefundsAsync());
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM credit_refund_requests;"));
        }
        finally { _options = originalOptions; _actor = originalActor; }
    }
}
