using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseExpenseSnapshotTests
{
    [Fact]
    public async Task ExpensePage_ConcurrentCancellationAndNewExpenseCannotMixRowsSummaryAndCashBalance()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-expense-snapshot-{Guid.NewGuid():N}.db");
        string connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            CommercialOperationContext actor;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(setup);
                setup.Products.Add(new ProductEntity { Id = "P", Barcode = "TEST", Name = "Prueba", Price = 20m, Cost = 5m, Stock = 10, Unit = "Pza" });
                await setup.SaveChangesAsync();
            }
            var factory = new TestDbContextFactory(options);
            var sales = new SaleService(factory, new CoOccurrenceQueue(), new SpyAuditService(), actor);
            await sales.SaveAsync(new SaleDto("FLOAT", "", [new("P", "Prueba", 20m, 5m, 1, "Pza", "General", 0m, 20m)],
                20m, 5m, 15m, 20m, 0m, "Efectivo", null, false, false, null, CashAmount: 20m, OperationId: "FLOAT"));
            var expenses = new ExpenseService(factory, actor);
            await expenses.CreateAsync(new("BEFORE", "General", 5m, "", "Original", "Efectivo", ""));
            var interleave = new ExpensePageObserver(async () =>
            {
                await expenses.CancelAsync("BEFORE", "Restituido", "VOID-BEFORE", true);
                await expenses.CreateAsync(new("DURING", "General", 2m, "", "Nueva salida", "Efectivo", ""));
            });
            var reading = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(interleave).Options;
            var page = await new ExpenseService(new TestDbContextFactory(reading), actor).GetPageAsync(0, activeShiftOnly: true);
            Assert.True(interleave.Committed);
            Assert.Equal(1, page.TotalCount);
            Assert.False(Assert.Single(page.Items).IsCancelled);
            Assert.Equal(5m, page.Summary!.TotalExpenses);
            Assert.Equal(15m, page.ActiveShift!.ExpectedBalance);
            Assert.Equal(1, interleave.DetailQueries);
            Assert.True(interleave.ScalarQueries > 0);
            var after = await expenses.GetPageAsync(0, activeShiftOnly: true);
            Assert.Equal(2, after.TotalCount);
            var cancelled = after.Items.Single(e => e.Id == "BEFORE");
            Assert.True(cancelled.IsCancelled);
            Assert.Equal("Restituido", cancelled.CancellationReason);
            Assert.NotNull(cancelled.CancelledAtUtc);
            Assert.NotEqual("Fecha sin verificar", cancelled.CancellationDateDisplay);
            Assert.Equal("TEST-REGISTER", cancelled.RegisterId);
            Assert.Equal("SHIFT-1", cancelled.ShiftId);
            Assert.Equal(2m, after.Summary!.TotalExpenses);
            Assert.Equal(18m, after.ActiveShift!.ExpectedBalance);
        }
        finally
        {
            string absolute = Path.GetFullPath(path);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("ticketfy-expense-snapshot-", Path.GetFileName(absolute), StringComparison.Ordinal);
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(absolute + suffix)) File.Delete(absolute + suffix);
        }
    }

    private sealed class ExpensePageObserver(Func<Task> write) : DbCommandInterceptor
    {
        private int _triggered;
        public bool Committed { get; private set; }
        public int DetailQueries { get; private set; }
        public int ScalarQueries { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM Expenses", StringComparison.Ordinal))
            {
                DetailQueries++;
                Assert.Contains("LIMIT", command.CommandText);
                Assert.NotNull(command.Transaction);
                if (Interlocked.Exchange(ref _triggered, 1) == 0) { await write(); Committed = true; }
            }
            else if (command.CommandText.Contains("FROM \"Expenses\"", StringComparison.Ordinal))
            {
                ScalarQueries++;
                Assert.DoesNotContain("\"Description\"", command.CommandText);
                Assert.DoesNotContain("\"Reference\"", command.CommandText);
                Assert.NotNull(command.Transaction);
            }
            return result;
        }
    }
}
