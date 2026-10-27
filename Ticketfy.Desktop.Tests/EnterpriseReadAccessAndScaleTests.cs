using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CashierCatalogAndSaleResponse_HideCostsButPersistRealHistoricalCost()
    {
        await using var db = Db();
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        var products = ProductsService();
        var product = (await products.GetByIdAsync("P"))!;
        Assert.False(product.CostsVisible);
        Assert.Equal(0m, product.Cost);
        Assert.Equal("Restringido", product.CostDisplay);
        Assert.All(await products.GetAllAsync(), p => Assert.Equal(0m, p.Cost));
        var saved = await Sales.SaveAsync(Sale("COST-PRIVATE"));
        Assert.False(saved.CostsVisible);
        Assert.Equal(0m, saved.TotalCost);
        Assert.Equal(0m, saved.Profit);
        Assert.All(saved.Items, i => Assert.Equal(0m, i.Cost));
        Assert.False((await Sales.GetByIdAsync(saved.Id))!.CostsVisible);
        Assert.All(await products.GetKardexAsync("P"), m => Assert.Equal(0m, m.UnitCost));
        Assert.Equal(6m, (await db.Sales.AsNoTracking().SingleAsync()).TotalCost);
        Assert.Equal(3m, (await db.SaleLines.SingleAsync()).UnitCost);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Sales.GetHistoryAsync());
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("unknown-role")]
    [InlineData("inactive")]
    public async Task ReadServices_RejectLockedUnknownOrInactiveSession(string state)
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync();
        if (state == "unknown-role") user.RoleString = "UNREGISTERED_ROLE";
        if (state == "inactive") user.IsActive = false;
        await db.SaveChangesAsync();
        var engine = new UserRolePermissionEngine();
        var session = new SessionManager();
        session.StartSession(engine.MapToModel(user));
        if (state == "locked") session.LockTerminal();
        var actor = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        var factory = new TestDbContextFactory(_options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProductService(factory, actor).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CustomerService(factory, actor).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SupplierService(factory, actor).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftNoteService(factory, actor).GetActiveNotesAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AttendanceService(factory, actor).ClockInAsync(user.Id.ToString()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PromotionService(factory, actor).GetActiveAsync());
    }

    [Fact]
    public async Task SalesHistory_QueriesLedgersInBatchesNotPerTicket()
    {
        await using (var db = Db())
        {
            (await db.Products.SingleAsync()).Stock = 1000;
            await db.SaveChangesAsync();
        }
        for (int i = 0; i < 75; i++) await Sales.SaveAsync(Sale("SCALE-" + i));
        var count = new QueryCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(count).Options;
        var service = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), _actor);
        var history = await service.GetHistoryAsync(100);
        Assert.Equal(75, history.Count);
        Assert.All(history, sale => { Assert.Single(sale.Payments!); Assert.Single(sale.Items); Assert.NotNull(sale.Items[0].SaleLineId); });
        Assert.InRange(count.Reads, 1, 11); // Budget, bounded payload and final authorization are fixed batch queries.
        int batchReads = count.Reads;
        count.Reads = 0;
        await service.GetHistoryAsync(1);
        Assert.Equal(batchReads, count.Reads); // The count must not grow with the number of tickets.
        count.Reads = 0;
        var period = await service.GetSalesByDateRangeAsync(Ticketfy.Core.Helpers.BusinessTime.Today, Ticketfy.Core.Helpers.BusinessTime.Today);
        Assert.Equal(75, period.Count);
        Assert.InRange(count.Reads, 1, 11);
    }

    [Fact]
    public async Task KitCatalog_LoadsSharedComponentsInBoundedQueryCount()
    {
        await using var db = Db();
        for (int i = 0; i < 75; i++)
        {
            string id = "KIT-" + i;
            db.Products.Add(new ProductEntity { Id = id, Barcode = id, Name = id, Price = 20m, IsKit = true, IsActive = true });
            db.ItemKits.Add(new ItemKitEntity { Id = id, ParentProductId = id, KitBarcode = id, Name = id, Price = 20m });
            db.ItemKitItems.Add(new ItemKitItemEntity { ItemKitId = id, ProductId = "P", Quantity = 2 });
        }
        await db.SaveChangesAsync();
        var count = new QueryCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(count).Options;
        var products = await new ProductService(new TestDbContextFactory(options), _actor).GetAllAsync();
        Assert.Equal(76, products.Count);
        Assert.All(products.Where(p => p.IsKit), p => Assert.Equal(10d, p.Stock));
        Assert.InRange(count.Reads, 1, 7);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
