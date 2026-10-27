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

public sealed class EnterpriseReportSnapshotTests
{
    [Fact]
    public async Task Report_SeesOneSnapshotWhileAnotherConnectionCommitsSale()
    {
        string root = Path.Combine(Path.GetTempPath(), "ticketfy-report-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "synthetic.db");
        string connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 2 }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        try
        {
            CommercialOperationContext actor;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                actor = CommercialTestSession.Create(db);
                db.Products.Add(new ProductEntity { Id = "P", Name = "Prueba", Barcode = "REPORT", Price = 20m, Cost = 5m, Stock = 10, Unit = "Pza" });
                await db.SaveChangesAsync();
            }
            var sales = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), actor);
            SaleDto Sale(string id) => new(id, "", [new("P", "Prueba", 20m, 5m, 1, "Pza", "General", 0m, 20m)],
                20m, 5m, 15m, 20m, 0m, "Efectivo", null, false, false, null, CashAmount: 20m, OperationId: id);
            await sales.SaveAsync(Sale("BEFORE"));
            var interceptor = new InterleavedSaleInterceptor(() => sales.SaveAsync(Sale("DURING")));
            var reportOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
            var report = await new ExpenseService(new TestDbContextFactory(reportOptions), actor).GetFinancialSummaryAsync();
            Assert.True(interceptor.Committed, "The concurrent writer must commit while the report's read transaction remains open.");
            Assert.Equal(20m, report.TotalRevenue);
            Assert.Equal(20m, report.CashRevenue);
            Assert.Equal(5m, report.TotalCostOfGoodsSold);
            var after = await new ExpenseService(new TestDbContextFactory(options), actor).GetFinancialSummaryAsync();
            Assert.Equal(40m, after.TotalRevenue);
            Assert.Equal(40m, after.CashRevenue);
            var pagingInterceptor = new InterleavedSaleInterceptor(() => sales.SaveAsync(Sale("DURING-PAGE")));
            var pagingOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(pagingInterceptor).Options;
            var page = await new SaleService(new TestDbContextFactory(pagingOptions), new CoOccurrenceQueue(), new SpyAuditService(), actor)
                .GetHistoryPageAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today, 0);
            Assert.True(pagingInterceptor.Committed);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal(2, page.Items.Count);
            Assert.Equal(40m, page.Hours.Sum(h => h.GrossAmount));
            Assert.DoesNotContain(page.Items, s => s.OperationId == "DURING-PAGE");
            Assert.Equal(3, (await sales.GetHistoryPageAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today, 0)).TotalCount);
            var shifts = new ShiftService(new TestDbContextFactory(options), actor);
            CashupEntity Count(string id) => new() { Id = id, OperationId = id, Type = "Parcial", ClosedCashAmount = 60m };
            await shifts.RecordCountAsync("SHIFT-1", Count("BEFORE-CUT"));
            var cashupInterceptor = new InterleavedSaleInterceptor(async () =>
            {
                await shifts.RecordCountAsync("SHIFT-1", Count("DURING-CUT"));
            }, "FROM \"cashups\"");
            var cashupOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(cashupInterceptor).Options;
            var cuts = await new ShiftService(new TestDbContextFactory(cashupOptions), actor)
                .GetCashupPageAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today, 0);
            Assert.True(cashupInterceptor.Committed);
            Assert.Equal(1, cuts.TotalCount);
            Assert.Equal("BEFORE-CUT", Assert.Single(cuts.Items).Id);
            Assert.Equal(60m, cuts.Items[0].TotalSales);
            Assert.Equal(2, (await shifts.GetCashupPageAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today, 0)).TotalCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InterleavedSaleInterceptor(Func<Task> write, string queryMarker = "FROM \"sales\"") : DbCommandInterceptor
    {
        private int _triggered;
        public bool Committed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(queryMarker, StringComparison.Ordinal) && Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                await write();
                Committed = true;
            }
            return result;
        }
    }
}
