using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services;
using Ticketfy.Services.Auth;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    // Each counter belongs to one test. IMaterializationInterceptor is a singleton
    // service: do not retain its per-test instance in EF's process-wide provider cache.
    // Keep ManyServiceProvidersCreatedWarning enabled; production options are unchanged.
    private DbContextOptions<AppDbContext> ObservedPurchaseOptions(IInterceptor observer) =>
        new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false)
            .AddInterceptors(observer).Options;

    private async Task SeedPurchaseDocumentAsync(string id, int rows, string notes = "", string name = "Producto")
    {
        await using var db = Db();
        db.Purchases.Add(new PurchaseEntity
        { Id = id, SupplierId = "S", SupplierName = "Proveedor", TotalCost = rows * 3m, Notes = notes, LedgerVersion = 1 });
        for (int i = 0; i < rows; i++)
            db.PurchaseItems.Add(new PurchaseItemEntity
            { Id = id + "-L" + i, PurchaseId = id, ProductId = "P", ProductName = name, Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("page")]
    [InlineData("all")]
    public async Task PurchaseDocument_TooManyLinesRemainVisibleWithoutMaterializingTheirDetail(string lookup)
    {
        await SeedPurchaseDocumentAsync("EXCESS", 1001);
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        var service = new PurchaseService(new TestDbContextFactory(options), _actor);
        var result = lookup switch
        {
            "page" => Assert.Single(await service.GetPageAsync(0)),
            "all" => Assert.Single(await service.GetAllAsync()),
            _ => (await service.GetByIdAsync("EXCESS"))!
        };
        Assert.Equal("EXCESS", result.Id); Assert.Equal(3003m, result.TotalCost);
        Assert.Empty(result.Items); Assert.Equal(0, observer.Items);
        Assert.False(result.DetailsAvailable); Assert.Contains("no verificable", result.DetailWarning);
    }

    [Fact]
    public async Task PurchaseDocument_OversizedNoteIsNotMaterializedOrChanged()
    {
        string huge = new('é', 2 * 1024 * 1024);
        await SeedPurchaseDocumentAsync("HUGE-NOTE", 1, huge);
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        var result = await new PurchaseService(new TestDbContextFactory(options), _actor).GetByIdAsync("HUGE-NOTE");
        Assert.NotNull(result); Assert.Equal(3m, result.TotalCost);
        Assert.True(result.Notes.Length <= 8192); Assert.False(observer.HugeNote);
        Assert.False(result.DetailsAvailable); Assert.Empty(result.Items);
        await using var verify = Db(); Assert.Equal(huge, (await verify.Purchases.SingleAsync()).Notes);
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task PurchaseDocument_ContradictoryOriginalCannotAuthorizeSupplierReturn()
    {
        await SeedPurchaseDocumentAsync("CONFLICT", 1);
        await using (var db = Db()) { (await db.Purchases.SingleAsync()).TotalCost = 100m; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(_options), _actor)
            .ReturnAsync("CONFLICT-L0", 1m, "Comprobar original", "RETURN-CONFLICT"));
        await using var verify = Db();
        Assert.Empty(await verify.SupplierReturns.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync()); Assert.Empty(await verify.AccountEntries.ToListAsync());
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("return")]
    public async Task PurchaseDocument_NulReferenceCannotAliasAnotherDocument(string action)
    {
        await SeedPurchaseDocumentAsync("TARGET", 1);
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        if (action == "read")
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetByIdAsync("TARGET\0different"));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReturnAsync("TARGET-L0\0different", 1m, "Referencia distinta", "RETURN-ALIAS"));
        await using var verify = Db();
        Assert.Empty(await verify.SupplierReturns.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.AccountEntries.ToListAsync()); Assert.Empty(await verify.CommandReceipts.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseDocument_PageBudgetsDeferDetailsWithoutLosingHeadersOrIndividualAccess(bool text)
    {
        int documents = text ? 18 : 11, rows = text ? 350 : 1000;
        for (int i = 0; i < documents; i++)
            await SeedPurchaseDocumentAsync("BUDGET-" + i, rows, name: text ? new string('é', 700) : "Producto");
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        var service = new PurchaseService(new TestDbContextFactory(options), _actor);
        var page = await service.GetPageAsync(0);
        Assert.Equal(documents, page.Count); Assert.Equal(documents * rows * 3m, page.Sum(p => p.TotalCost));
        var deferred = page.First(p => !p.DetailsAvailable);
        Assert.Contains("consulta individual", deferred.DetailWarning);
        Assert.True(observer.Items <= PurchaseDocumentReader.PageLines);
        if (!text) Assert.Equal(PurchaseDocumentReader.PageLines, observer.Items);
        var detail = await service.GetByIdAsync(deferred.Id);
        Assert.NotNull(detail); Assert.True(detail.DetailsAvailable); Assert.Equal(rows, detail.Items.Count);
        Assert.Equal(detail.TotalCost, detail.Items.Sum(i => i.TotalPrice));
        Assert.Empty(await service.GetPageAsync(1));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("zero")]
    [InlineData("infinite")]
    [InlineData("cost")]
    [InlineData("subtotal")]
    [InlineData("missing")]
    public async Task PurchaseDocument_InvalidOriginalIsIsolatedAndCannotAuthorizeReturn(string kind)
    {
        await SeedPurchaseDocumentAsync("BAD", kind == "missing" ? 0 : 1);
        await SeedPurchaseDocumentAsync("GOOD", 1);
        await using (var db = Db())
        {
            if (kind != "missing")
            {
                var line = await db.PurchaseItems.SingleAsync(i => i.PurchaseId == "BAD");
                if (kind == "text") line.ProductName = new string('x', 2 * 1024 * 1024);
                if (kind == "zero") line.Quantity = 0;
                if (kind == "infinite") line.Quantity = double.PositiveInfinity;
                if (kind == "cost") line.UnitPrice = 0m;
                if (kind == "subtotal") line.TotalPrice = 2m;
                await db.SaveChangesAsync();
            }
        }
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        var service = new PurchaseService(new TestDbContextFactory(options), _actor);
        var page = await service.GetPageAsync(0);
        Assert.Equal(2, page.Count);
        Assert.True(page.Single(p => p.Id == "GOOD").DetailsAvailable);
        Assert.False(page.Single(p => p.Id == "BAD").DetailsAvailable);
        Assert.Empty(page.Single(p => p.Id == "BAD").Items);
        if (kind == "text") Assert.Equal(1, observer.Items);
        if (kind != "missing")
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnAsync("BAD-L0", 1m, "Revisión original", "INVALID-RETURN"));
        await using var verify = Db();
        Assert.Empty(await verify.SupplierReturns.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("quantity")]
    [InlineData("zero")]
    public async Task PurchaseDocument_ContradictoryEarlierReturnsCannotCreateAnotherCredit(string kind)
    {
        await SeedPurchaseDocumentAsync("RETURNS", 1);
        await using (var db = Db())
        {
            db.SupplierReturns.Add(new SupplierReturnEntity
            {
                OperationId = "OLD",
                PurchaseId = "RETURNS",
                PurchaseItemId = "RETURNS-L0",
                SupplierId = "S",
                Quantity = kind == "zero" ? 0m : kind == "quantity" ? 2m : 0.5m,
                Amount = 100m
            });
            await db.SaveChangesAsync();
        }
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(new TestDbContextFactory(options), _actor)
            .ReturnAsync("RETURNS-L0", 0.25m, "Revisión de devoluciones", "NEW"));
        Assert.Equal(0, observer.Returns);
        await using var verify = Db(); Assert.Single(await verify.SupplierReturns.ToListAsync());
        Assert.Empty(await verify.AccountEntries.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
    }

    [Fact]
    public async Task PurchaseDocument_ValidReturnsStreamTheirHistoryAndRepeatExactlyOnce()
    {
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var purchase = await service.RegisterPurchaseAsync(new("RETURNS", "S", "Proveedor", "RETURNS", "", 0m, "",
            [new("", "", "P", "Producto", 3m, 2, 6m)]));
        string lineId = purchase.Items.Single().Id;
        await service.ReturnAsync(lineId, 1m, "Primera mitad", "FIRST");
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        service = new PurchaseService(new TestDbContextFactory(options), _actor);
        await service.ReturnAsync(lineId, 1m, "Segunda mitad", "SECOND");
        Assert.Equal(0, observer.Returns);
        await service.ReturnAsync(lineId, 1m, "Segunda mitad", "SECOND");
        Assert.Equal(0, observer.Returns);
        await using var verify = Db();
        Assert.Equal(2, await verify.SupplierReturns.CountAsync());
        Assert.Equal(0m, (await verify.AccountEntries.ToListAsync()).Sum(e => e.Amount));
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task PurchaseDocument_PaymentStreamsTheBalanceAndReplayDoesNotReadAnOversizedOldReference()
    {
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        await service.RegisterPurchaseAsync(new("PAY-READ", "S", "Proveedor", "F-PAY", "", 0m, "",
            [new("L", "", "P", "Producto", 3m, 1, 3m)]));
        var observer = new PurchaseDocumentProbe();
        var options = ObservedPurchaseOptions(observer);
        service = new PurchaseService(new TestDbContextFactory(options), _actor);
        await service.PayAsync("PAY-READ", 3m, "Tarjeta", "SYNTHETIC-BANK", "PAY-ONCE");
        Assert.Equal(0, observer.Accounts); Assert.Equal(0, observer.Payments);
        string legacy = new('x', 2 * 1024 * 1024);
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supplier_payments SET \"Reference\" = {legacy} WHERE \"OperationId\" = {"PAY-ONCE"}");
        await service.PayAsync("PAY-READ", 3m, "Tarjeta", "SYNTHETIC-BANK", "PAY-ONCE");
        Assert.Equal(0, observer.Accounts); Assert.Equal(0, observer.Payments);
        await using var verify = Db();
        Assert.Single(await verify.SupplierPayments.ToListAsync());
        Assert.Equal(legacy, (await verify.SupplierPayments.SingleAsync()).Reference);
        Assert.Equal(0m, (await verify.AccountEntries.ToListAsync()).Sum(e => e.Amount));
        Assert.Equal(21d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task PurchaseDocument_LogoutDuringDetailReadCannotReturnTheOldSessionsData()
    {
        await SeedPurchaseDocumentAsync("PRIVATE", 1);
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var actor = new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER");
        var observer = new PurchaseFootprintInterleave(() => { session.ClearSession(); return Task.CompletedTask; });
        var options = ObservedPurchaseOptions(observer);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PurchaseService(new TestDbContextFactory(options), actor).GetByIdAsync("PRIVATE"));
        Assert.True(observer.Called);
    }

    [Fact]
    public async Task PurchaseDocument_HeaderFootprintAndDetailRemainConsistentDuringWalWrite()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var db = new AppDbContext(options))
            {
                db.Purchases.Add(new PurchaseEntity { Id = "WAL", SupplierId = "S", TotalCost = 3m });
                db.PurchaseItems.Add(new PurchaseItemEntity { Id = "L1", PurchaseId = "WAL", ProductId = "P", ProductName = "Uno", Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
                await db.SaveChangesAsync();
            }
            var observer = new PurchaseFootprintInterleave(async () =>
            {
                await using var db = new AppDbContext(options);
                (await db.Purchases.SingleAsync()).TotalCost = 6m;
                db.PurchaseItems.Add(new PurchaseItemEntity { Id = "L2", PurchaseId = "WAL", ProductId = "P", ProductName = "Dos", Quantity = 1, UnitPrice = 3m, TotalPrice = 3m });
                await db.SaveChangesAsync();
            });
            var readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options;
            var original = await new PurchaseService(new TestDbContextFactory(readOptions), actor).GetByIdAsync("WAL");
            Assert.True(observer.Called); Assert.True(original!.DetailsAvailable);
            Assert.Equal(3m, original.TotalCost); Assert.Single(original.Items);
            var current = await new PurchaseService(new TestDbContextFactory(options), actor).GetByIdAsync("WAL");
            Assert.True(current!.DetailsAvailable); Assert.Equal(6m, current.TotalCost); Assert.Equal(2, current.Items.Count);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseDocument_HeaderProjectionKeepsIndexedLookupBeforeBoundingText(bool page)
    {
        await SeedPurchaseDocumentAsync("PLAN", 1);
        var observer = new PurchaseHeaderPlanProbe();
        var options = ObservedPurchaseOptions(observer);
        var service = new PurchaseService(new TestDbContextFactory(options), _actor);
        if (page) await service.GetPageAsync(0); else await service.GetByIdAsync("PLAN");
        Assert.NotNull(observer.Sql);
        using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + observer.Sql;
        foreach (var parameter in observer.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.Contains(details, p => p.Contains("SEARCH p USING", StringComparison.Ordinal) &&
            p.Contains(page ? "INTEGER PRIMARY KEY" : "sqlite_autoindex_Purchases", StringComparison.Ordinal));
    }

    private sealed class PurchaseHeaderPlanProbe : DbCommandInterceptor
    {
        public string? Sql;
        public List<(string Name, object Value)> Parameters = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CASE WHEN length(CAST(COALESCE(p.", StringComparison.Ordinal))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value ?? DBNull.Value)).ToList();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PurchaseFootprintInterleave(Func<Task> action) : DbCommandInterceptor
    {
        public bool Called;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Called && command.CommandText.Contains("AS \"ItemBytes\"", StringComparison.Ordinal))
            { Called = true; await action(); }
            return result;
        }
    }

    private sealed class PurchaseDocumentProbe : IMaterializationInterceptor
    {
        public int Items;
        public bool HugeNote;
        public int Returns;
        public int Accounts;
        public int Payments;
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is PurchaseItemEntity) Items++;
            if (entity is SupplierReturnEntity) Returns++;
            if (entity is AccountEntryEntity) Accounts++;
            if (entity is SupplierPaymentEntity) Payments++;
            if (entity is PurchaseEntity purchase) HugeNote |= purchase.Notes.Length > 8192;
            return entity;
        }
    }
}
