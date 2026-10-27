using Microsoft.EntityFrameworkCore;
using Avalonia.Headless.XUnit;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task Ui_InventorySnapshotHistoryLoadsDetailsAfterBoundedHeaderQuery()
    {
        var service = new InventorySnapshotService(new TestDbContextFactory(_options), _actor);
        await service.CreateSnapshotAsync("Synthetic", "UI-SNAPSHOT");
        var model = new Ticketfy.ViewModels.InventorySnapshotsViewModel(service);
        await model.LoadSnapshotsAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Single(model.Snapshots);
        Assert.NotNull(model.SelectedSnapshot);
        Assert.Equal("P", Assert.Single(model.SelectedSnapshot.Items).ProductId);
        Assert.Equal(60m, model.SelectedSnapshot.TotalValue);
        Assert.False(model.HasMore);
    }

    [Fact]
    public async Task InventoryPage_TotalsCoverAllFilteredRowsNotJustVisiblePageAndHideCosts()
    {
        await using var db = Db();
        for (int i = 0; i < 225; i++)
            db.Products.Add(new ProductEntity
            {
                Id = "INV-" + i,
                Barcode = "INV-" + i,
                Name = "Inventory " + i.ToString("D3"),
                Category = "Decimal",
                Stock = 1.125,
                IsBulk = true,
                Cost = 1.11m,
                Price = 2.23m
            });
        await db.SaveChangesAsync();
        var service = ProductsService();
        var first = await service.GetInventoryPageAsync(0, search: "Decimal");
        var last = await service.GetInventoryPageAsync(2, search: "Decimal");
        Assert.Equal(100, first.Items.Count); Assert.True(first.HasMore);
        Assert.Equal(25, last.Items.Count); Assert.False(last.HasMore);
        Assert.Equal(225m * 1.125m * 1.11m, first.StockCost);
        Assert.Equal(225m * 1.125m * 2.23m, first.StockSale);
        Assert.Equal(first.StockCost, last.StockCost);
        Assert.Equal("INV-224", Assert.Single((await service.GetInventoryPageAsync(0, search: "Inventory 224")).Items).Id);
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        var redacted = await service.GetInventoryPageAsync(0);
        Assert.False(redacted.CostsVisible); Assert.Equal(0m, redacted.StockCost);
        Assert.All(redacted.Items, p => Assert.False(p.CostsVisible));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetInventoryPageAsync(-1));
    }

    [Fact]
    public async Task InventorySnapshots_AreAuthorizedIdempotentAuditedAndExcludeVirtualKits()
    {
        await using var db = Db();
        db.Products.Add(new ProductEntity { Id = "VIRTUAL", Name = "Virtual", Barcode = "VIRTUAL", IsKit = true, Stock = 100, Cost = 999m });
        db.Products.Add(new ProductEntity { Id = "BULK-SNAPSHOT", Name = "Bulk", Barcode = "BULK-SNAPSHOT", IsBulk = true, Stock = 1.125, Cost = 2m });
        await db.SaveChangesAsync();
        var factory = new TestDbContextFactory(_options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new InventorySnapshotService(factory).GetSnapshotsAsync());
        var service = new InventorySnapshotService(factory, _actor);
        var captured = await service.CreateSnapshotAsync("Test capture", "SNAPSHOT-1");
        await service.CreateSnapshotAsync("Test capture", "SNAPSHOT-1");
        Assert.Equal(62.25m, captured.TotalValue); Assert.Equal(2, captured.TotalItems);
        Assert.Equal(1, await db.InventorySnapshots.CountAsync());
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "INVENTORY_SNAPSHOT_CREATED"));
        Assert.Empty(Assert.Single(await service.GetSnapshotsAsync()).Items);
        Assert.Equal(1.125m, (await service.GetSnapshotDetailsAsync("SNAPSHOT-1"))!.Items.Single(i => i.ProductId == "BULK-SNAPSHOT").Quantity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateSnapshotAsync("changed", "SNAPSHOT-1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetSnapshotsAsync(-1));
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnapshotsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnapshotDetailsAsync("SNAPSHOT-1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateSnapshotAsync("denied", "NOAUTH"));
        var stored = await db.InventorySnapshots.SingleAsync(); stored.TotalValue = 0m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PredictiveReads_RequireSessionRedactCostsAndUseNetPhysicalConsumption()
    {
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        var anonymous = new PredictiveIntelligenceService(factory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonymous.GetTopCorrelatedProductAsync("A", []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonymous.GetUrgentRestockAlertsAsync());
        db.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = "A", ProductoB = "P", Frecuencia = 10 });
        await db.SaveChangesAsync();
        await Sales.SaveAsync(Sale("CONSUMPTION") with
        {
            Items = [Sale("CONSUMPTION").Items[0] with { Quantity = 20, TotalPrice = 200m }],
            PaidAmount = 200m,
            Total = 200m,
            CashAmount = 200m
        });
        var service = new PredictiveIntelligenceService(factory, _actor);
        Assert.Single(await service.GetUrgentRestockAlertsAsync());
        await Sales.ProcessPartialReturnAsync((await db.Sales.SingleAsync()).Id, "P", 10, "Return", operationId: "RESTORE-DEMAND");
        Assert.Empty(await service.GetUrgentRestockAlertsAsync());
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        var suggestion = await service.GetTopCorrelatedProductAsync("A", []);
        Assert.NotNull(suggestion); Assert.Equal(0m, suggestion.Cost); Assert.False(suggestion.CostsVisible);
        Assert.Null(await service.GetTopCorrelatedProductAsync("A", ["P"]));
        (await db.Products.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        Assert.Null(await service.GetTopCorrelatedProductAsync("A", []));
    }
}
