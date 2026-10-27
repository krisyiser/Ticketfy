using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseLedgerSequenceTests
{
    [Fact]
    public async Task LedgerSequence_ConcurrentWritersRollbackAndFailedBatchCannotDuplicateOrPublishPartialRecords()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-ledger-sequence-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 20 }.ToString()).Options;
        try
        {
            await using (var setup = new AppDbContext(options)) await setup.Database.MigrateAsync();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
            {
                await using var db = new AppDbContext(options);
                for (int i = 0; i < 8; i++)
                {
                    db.AccountEntries.Add(new AccountEntryEntity { Id = $"A-{worker}-{i}", OperationId = $"OP-{worker}-{i}", AccountId = "C", Amount = 1m, PostingSequence = -99 });
                    db.InventoryMovements.Add(new InventoryMovementEntity { Id = $"I-{worker}-{i}", OperationId = $"OP-{worker}-{i}", ProductId = "P", Quantity = 1m, PostingSequence = 999999 });
                }
                if (worker % 2 == 0) await db.SaveChangesAsync();
                else db.SaveChanges();
            })));
            await using (var rollback = new AppDbContext(options))
            {
                await using var tx = await rollback.Database.BeginTransactionAsync();
                rollback.AccountEntries.Add(new AccountEntryEntity { Id = "ROLLBACK-A", OperationId = "ROLLBACK", AccountId = "C", Amount = 123m });
                rollback.InventoryMovements.Add(new InventoryMovementEntity { Id = "ROLLBACK-I", OperationId = "ROLLBACK", ProductId = "P", Quantity = 123m });
                await rollback.SaveChangesAsync();
                await tx.RollbackAsync();
            }
            await using (var failing = new AppDbContext(options))
            {
                failing.AccountEntries.Add(new AccountEntryEntity { Id = "A-0-0", OperationId = "FAILED", AccountId = "C", Amount = 500m });
                failing.InventoryMovements.Add(new InventoryMovementEntity { Id = "FAILED-I", OperationId = "FAILED", ProductId = "P", Quantity = 500m });
                await Assert.ThrowsAsync<DbUpdateException>(() => failing.SaveChangesAsync());
            }
            await using var check = new AppDbContext(options);
            Assert.Equal(Enumerable.Range(1, 64).Select(i => (long)i),
                await check.AccountEntries.OrderBy(e => e.PostingSequence).Select(e => e.PostingSequence).ToListAsync());
            Assert.Equal(Enumerable.Range(1, 64).Select(i => (long)i),
                await check.InventoryMovements.OrderBy(e => e.PostingSequence).Select(e => e.PostingSequence).ToListAsync());
            Assert.Equal(64m, (await check.AccountEntries.ToListAsync()).Sum(e => e.Amount));
            check.AccountEntries.Add(new AccountEntryEntity { Id = "AFTER-FAILURE", OperationId = "AFTER-FAILURE", AccountId = "C", Amount = 1m });
            await check.SaveChangesAsync();
            Assert.Equal(65, await check.AccountEntries.MaxAsync(e => e.PostingSequence));
        }
        finally { DeleteSyntheticDatabase(path); }
    }

    [Fact]
    public async Task LedgerPage_ConcurrentCommitCannotMixItsRowsWithNewerBalanceOrRecordedStock()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-ledger-snapshot-{Guid.NewGuid():N}.db");
        string connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            CommercialOperationContext actor;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.MigrateAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(setup);
                setup.Customers.Add(new CustomerEntity { Id = "C", Name = "Synthetic", Debt = 1m });
                setup.AccountEntries.Add(new AccountEntryEntity { Id = "FIRST", OperationId = "FIRST", AccountId = "C", Amount = 1m });
                setup.Products.Add(new ProductEntity { Id = "P", Name = "Synthetic", Barcode = "SYNTHETIC", Stock = 1 });
                setup.InventoryMovements.Add(new InventoryMovementEntity { Id = "FIRST", OperationId = "FIRST", ProductId = "P", Quantity = 1m, StockAfter = 1m });
                await setup.SaveChangesAsync();
            }
            async Task WriteAsync(string id)
            {
                await using var writer = new AppDbContext(options);
                (await writer.Customers.SingleAsync()).Debt += 1m;
                var product = await writer.Products.SingleAsync();
                var before = (decimal)product.Stock;
                product.Stock++;
                writer.AccountEntries.Add(new AccountEntryEntity { Id = id, OperationId = id, AccountId = "C", Amount = 1m });
                writer.InventoryMovements.Add(new InventoryMovementEntity { Id = id, OperationId = id, ProductId = "P", Quantity = 1m, StockBefore = before, StockAfter = before + 1m });
                await writer.SaveChangesAsync();
            }
            var accountWrite = new InterleavedLedgerWrite(() => WriteAsync("DURING-ACCOUNT"), "account_entries");
            var readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(accountWrite).Options;
            var first = await new CustomerService(new TestDbContextFactory(readOptions), actor).GetStatementPageAsync("C");
            Assert.True(accountWrite.Committed);
            Assert.Single(first.Entries); Assert.Equal(1m, first.Balance); Assert.Equal(1m, first.RecordedBalance);
            Assert.False(first.HasUnreconciledHistory); Assert.False(first.HasNewerEntries);
            var stockWrite = new InterleavedLedgerWrite(() => WriteAsync("DURING-STOCK"), "inventory_movements");
            readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(stockWrite).Options;
            var stock = await new ProductService(new TestDbContextFactory(readOptions), actor).GetKardexPageAsync("P");
            Assert.True(stockWrite.Committed);
            Assert.Equal(2, stock.TotalCount); Assert.Equal(2m, stock.LedgerStock); Assert.Equal(2m, stock.RecordedStock);
            Assert.False(stock.HasUnreconciledHistory);
            Assert.Equal(3m, (await new CustomerService(new TestDbContextFactory(options), actor).GetStatementPageAsync("C")).Balance);
        }
        finally { DeleteSyntheticDatabase(path); }
    }

    private sealed class InterleavedLedgerWrite(Func<Task> write, string table) : DbCommandInterceptor
    {
        private int _triggered;
        public bool Committed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _triggered, 1) == 0) { await write(); Committed = true; }
            return result;
        }
    }

    private static void DeleteSyntheticDatabase(string path)
    {
        string absolute = Path.GetFullPath(path);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("ticketfy-ledger-", Path.GetFileName(absolute), StringComparison.Ordinal);
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(absolute + suffix)) File.Delete(absolute + suffix);
    }
}
