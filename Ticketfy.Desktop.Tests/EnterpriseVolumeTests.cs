using System.Diagnostics;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Xunit.Abstractions;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseVolumeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PeriodReportsRemainCorrectWithOneHundredThousandOlderSyntheticTickets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var actor = CommercialTestSession.Create(db);
        db.Products.Add(new ProductEntity { Id = "VOLUME-P", Name = "Synthetic", Barcode = "VOLUME", Price = 10m, Cost = 3m, Stock = 100 });
        db.Sales.Add(new SaleEntity { Id = "VOLUME-SEED", OperationId = "VOLUME-SEED", Date = "2019-01-01T12:00:00Z", Total = 10m, TotalCost = 3m, Profit = 7m, LedgerVersion = 0 });
        await db.SaveChangesAsync();
        // Clone the synthetic legacy fixture using current EF column metadata; never touch an installed database.
        var table = StoreObjectIdentifier.Table("sales", null);
        var columns = db.Model.FindEntityType(typeof(SaleEntity))!.GetProperties().Select(p => p.GetColumnName(table)!).ToArray();
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
        string columnList = string.Join(",", columns.Select(Quote));
        string expressions = string.Join(",", columns.Select(c => c is "id" or "operation_id" ? "'VOLUME-' || n" : "s." + Quote(c)));
        await using (var populate = connection.CreateCommand())
        {
            populate.CommandText = $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<100000) INSERT INTO sales ({columnList}) SELECT {expressions} FROM seq CROSS JOIN sales s WHERE s.id='VOLUME-SEED';";
            await populate.ExecuteNonQueryAsync();
        }
        var factory = new TestDbContextFactory(options);
        var sales = new SaleService(factory, new CoOccurrenceQueue(), new SpyAuditService(), actor);
        var saved = await sales.SaveAsync(new SaleDto("LIVE", "", [new("VOLUME-P", "Synthetic", 10m, 3m, 2, "Pza", "General", 0m, 20m)],
            20m, 0m, 0m, 20m, 0m, "Efectivo", null, false, false, null, CashAmount: 20m, OperationId: "VOLUME-LIVE"));
        var watch = Stopwatch.StartNew();
        var period = await sales.GetSalesByDateRangeAsync(BusinessTime.Today, BusinessTime.Today);
        long periodMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var cashiers = await sales.GetCashierPerformanceReportAsync(BusinessTime.Today, BusinessTime.Today);
        long cashiersMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var profit = await new ExpenseService(factory, actor).CalculateTrueNetProfitAsync(BusinessTime.Today, BusinessTime.Today);
        long profitMs = watch.ElapsedMilliseconds;
        var productTable = StoreObjectIdentifier.Table("products", null);
        var productColumns = db.Model.FindEntityType(typeof(ProductEntity))!.GetProperties().Select(p => p.GetColumnName(productTable)!).ToArray();
        string productList = string.Join(",", productColumns.Select(Quote));
        string productExpressions = string.Join(",", productColumns.Select(c => c switch
        { "id" => "'CAT-' || n", "barcode" => "'CODE-' || n", "name" => "'Product ' || n", _ => "p." + Quote(c) }));
        await using (var populate = connection.CreateCommand())
        {
            populate.CommandText = $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<100000) INSERT INTO products ({productList}) SELECT {productExpressions} FROM seq CROSS JOIN products p WHERE p.id='VOLUME-P';";
            await populate.ExecuteNonQueryAsync();
        }
        var products = new ProductService(factory, actor);
        watch.Restart();
        var page = await products.GetPageAsync(0);
        long pageMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var lastProduct = await products.GetByBarcodeAsync("CODE-100000");
        long barcodeMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var inventory = await products.GetInventoryPageAsync(0);
        long inventoryMs = watch.ElapsedMilliseconds;
        Assert.Equal(100, inventory.Items.Count); Assert.True(inventory.HasMore);
        Assert.Equal(100001m * 98m * 3m, inventory.StockCost);
        Assert.Equal(60, page.Items.Count); Assert.True(page.HasMore);
        Assert.Equal("CAT-100000", lastProduct!.Id);
        Assert.Equal("CAT-100000", Assert.Single((await products.GetPageAsync(0, search: "product 100000")).Items).Id);
        Assert.Equal(100002, await db.Sales.CountAsync());
        Assert.Equal(saved.Id, Assert.Single(period).Id);
        Assert.Single(cashiers);
        Assert.Equal(20m, profit.NetSales);
        var historyPage = await sales.GetHistoryPageAsync(BusinessTime.Today, BusinessTime.Today, 0);
        Assert.Equal(1, historyPage.TotalCount);
        Assert.Equal(saved.Id, Assert.Single(historyPage.Items).Id);
        Assert.Equal(20m, historyPage.Hours.Sum(h => h.GrossAmount));
        var scalarQueries = new ScalarReportQueries();
        var scalarOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(scalarQueries).Options;
        var financial = new ExpenseService(new TestDbContextFactory(scalarOptions), actor);
        watch.Restart();
        var allTime = await financial.GetFinancialSummaryAsync();
        long allTimeMs = watch.ElapsedMilliseconds;
        Assert.Equal(1_000_030m, allTime.TotalRevenue);
        Assert.Equal(300_009m, allTime.TotalCostOfGoodsSold);
        Assert.Equal(700_021m, allTime.NetProfit);
        Assert.Equal(20m, allTime.CashRevenue); // Legacy fixtures contain no verifiable cash tender.
        var allCashiers = await new SaleService(new TestDbContextFactory(scalarOptions), new CoOccurrenceQueue(), new SpyAuditService(), actor)
            .GetCashierPerformanceReportAsync();
        Assert.Equal(100002, allCashiers.Sum(c => c.TicketCount));
        Assert.Equal(allTime.TotalRevenue, allCashiers.Sum(c => c.TotalRevenue));
        await using var scalarContext = new AppDbContext(scalarOptions);
        var productivity = await new PerformanceAnalyticsService(scalarContext, actor).CalculateTrueCashierProductivityAsync(DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(allTime.TotalRevenue, productivity.Sum(p => p.GrossSales));
        Assert.Equal(100002, productivity.Sum(p => p.TotalTicketsProcessed));
        Assert.Equal(100001, productivity.Single(p => !p.AttributionVerified).TotalTicketsProcessed);
        Assert.True(scalarQueries.SalesReads >= 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InventorySnapshotService(factory, actor)
            .CreateSnapshotAsync("Over budget", "OVER-BUDGET"));
        Assert.Empty(await db.InventorySnapshots.ToListAsync());
        Assert.False(await db.CommandReceipts.AnyAsync(c => c.OperationId == "OVER-BUDGET"));
        output.WriteLine($"Synthetic in-memory dataset: 100001 old tickets + 1 current, 100001 products. Period={periodMs}ms, cashier={cashiersMs}ms, profit={profitMs}ms, catalog page={pageMs}ms, barcode={barcodeMs}ms, inventory with full totals={inventoryMs}ms, all-time financial={allTimeMs}ms. This is not an installed-hardware throughput certification.");
    }

    private sealed class ScalarReportQueries : DbCommandInterceptor
    {
        public int SalesReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"sales\"", StringComparison.Ordinal))
            {
                SalesReads++;
                Assert.DoesNotContain("\"items\"", command.CommandText);
                Assert.DoesNotContain("\"giftcard_number\"", command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }
}
