using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Inventory;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task InventoryImport_RejectsExcessBeforeMutationAndReadsOnlyBoundedInput()
    {
        var products = ProductsService();
        var product = (await products.GetByIdAsync("P"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.BulkSaveAsync(
            Enumerable.Range(0, InventoryWorkloadLimits.ImportRows + 1).Select(i => product with { Id = $"IMPORT-{i}", Barcode = $"IMPORT-{i}" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync(new string('X', InventoryWorkloadLimits.CsvCharacters + 1)));
        using var oversized = new StringReader(new string('X', InventoryWorkloadLimits.CsvCharacters + 50));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InventoryWorkloadLimits.ReadCsvAsync(oversized));
        Assert.Equal(49, oversized.ReadToEnd().Length);
        using var small = new StringReader("valid bounded input");
        Assert.Equal("valid bounded input", await InventoryWorkloadLimits.ReadCsvAsync(small));
        var excessiveRows = string.Join("\n", Enumerable.Range(0, InventoryWorkloadLimits.ImportRows + 1).Select(i => $"NEW-{i},Name,1.00,2.00,0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync(excessiveRows));
        await using var db = Db();
        Assert.Single(await db.Products.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task InventoryCsv_HandlesQuotedMultilineFieldsAndRejectsMalformedFileAtomically()
    {
        var products = ProductsService();
        Assert.Equal(1, await products.ImportFromCsvTextAsync("codigo,nombre,costo,precio,stock,categoria,unidad\nQ-1,\"Arroz, \"\"extra\"\"\nblanco\",1.11,2.22,1.125,Granel,kg"));
        var imported = await products.GetByBarcodeAsync("Q-1");
        Assert.NotNull(imported);
        Assert.Equal("Arroz, \"extra\"\nblanco", imported.Name);
        Assert.Equal(1.125, imported.Stock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync("OK-1,Valid,1,2,0\nBAD-1,\"unclosed,1,2,0"));
        Assert.Null(await products.GetByBarcodeAsync("OK-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync("DUP,Name,1,2,0\nDUP,Other,1,2,0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync("EXTRA,Name,1,2,0,Category,pza,ignored"));
    }

    [AvaloniaFact]
    public async Task InventoryChecklist_RequiresBoundedPhysicalFilterAndReportsUnavailablePrintWindow()
    {
        await using var db = Db();
        for (int i = 0; i < InventoryWorkloadLimits.PrintableRows; i++)
            db.Products.Add(new ProductEntity { Id = $"PRINT-{i:D3}", Name = $"Printable {i:D3}", Barcode = $"PRINT-{i:D3}", Category = "Print", Stock = i });
        db.Products.Add(new ProductEntity { Id = "PRINT-KIT", Name = "Printable virtual", IsKit = true, Category = "Print" });
        db.Products.Add(new ProductEntity { Id = "PRINT-INACTIVE", Name = "Printable inactive", IsActive = false, Category = "Print" });
        await db.SaveChangesAsync();
        var products = ProductsService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.GetInventoryChecklistAsync());
        Assert.Equal(InventoryWorkloadLimits.PrintableRows, (await products.GetInventoryChecklistAsync("Print")).Count);
        var filtered = await products.GetInventoryChecklistAsync("PRINT-499");
        Assert.Equal("PRINT-499", Assert.Single(filtered).Id);
        var low = await products.GetInventoryChecklistAsync("Print", true);
        Assert.Equal(6, low.Count); // Default <= 5, excluding inactive/virtual records.
        var actions = new InventoryActionsViewModel(products, new NoNetworkCatalog());
        await actions.PrintChecklistAsync();
        Assert.True(actions.IsFeedbackError);
        Assert.Contains("Acote", actions.FeedbackMessage);
        await actions.PrintChecklistAsync("PRINT-499");
        Assert.True(actions.IsFeedbackError);
        Assert.Contains("No se envió", actions.FeedbackMessage);
    }

    [AvaloniaFact]
    public async Task InventorySnapshot_BatchesAtomicallyAndPagesImmutableDetailsWithExactValuation()
    {
        await using var db = Db();
        for (var i = 0; i < 701; i++) db.Products.Add(new ProductEntity
        {
            Id = $"CAPTURE-{i:D4}",
            Barcode = $"CAPTURE-{i:D4}",
            Name = $"Bulk {i:D4}",
            Cost = 1.11m,
            Stock = 1.125,
            IsBulk = true,
            IsActive = i % 2 == 0
        });
        await db.SaveChangesAsync();
        var tracker = new TrackingBudgetObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(tracker).Options;
        var service = new InventorySnapshotService(new TestDbContextFactory(options), _actor);
        // The second batch fails after the header and first batch have been saved inside the transaction.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_capture_second_batch BEFORE INSERT ON InventorySnapshotItems
            WHEN NEW.ProductId = 'CAPTURE-0600' BEGIN SELECT RAISE(ABORT, 'synthetic batch failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateSnapshotAsync("Synthetic capture", "BATCH-CAPTURE"));
        Assert.Empty(await db.InventorySnapshots.ToListAsync());
        Assert.Empty(await db.InventorySnapshotItems.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_capture_second_batch;");
        var snapshot = await service.CreateSnapshotAsync("Synthetic capture", "BATCH-CAPTURE");
        await service.CreateSnapshotAsync("Synthetic capture", "BATCH-CAPTURE");
        Assert.Equal(702, snapshot.TotalItems);
        Assert.Equal(decimal.Round(60m + 701m * 1.11m * 1.125m, 2, MidpointRounding.AwayFromZero), snapshot.TotalValue);
        Assert.InRange(tracker.MaximumTracked, 1, InventoryWorkloadLimits.SnapshotBatch + 5);
        Assert.Equal(702, await db.InventorySnapshotItems.CountAsync());
        Assert.Single(await db.TransactionJournal.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSnapshotDetailsAsync(snapshot.Id));
        var seen = new HashSet<string>();
        for (var page = 0; page < 8; page++)
        {
            var part = await service.GetSnapshotPageAsync(snapshot.Id, page);
            Assert.NotNull(part);
            Assert.Equal(page != 7, part.HasMore);
            Assert.Equal(snapshot.TotalValue, part.Snapshot.TotalValue);
            foreach (var item in part.Snapshot.Items) Assert.True(seen.Add(item.ProductId));
        }
        Assert.Equal(702, seen.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetSnapshotPageAsync(snapshot.Id, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetSnapshotPageAsync(snapshot.Id, 0, 201));
        Assert.Null(await service.GetSnapshotPageAsync("missing", 0));
        var model = new InventorySnapshotsViewModel(service);
        await model.LoadSnapshotsAsync();
        Assert.Equal(100, model.SelectedSnapshot!.Items.Count);
        Assert.True(model.HasMoreDetails);
        await model.NextDetailPageCommand.ExecuteAsync(null);
        Assert.Equal(1, model.DetailPage);
        Assert.Contains("702", model.DetailPageLabel);
        await model.PreviousDetailPageCommand.ExecuteAsync(null);
        Assert.Equal(0, model.DetailPage);
    }

    private sealed class TrackingBudgetObserver : SaveChangesInterceptor
    {
        public int MaximumTracked { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaximumTracked = Math.Max(MaximumTracked, eventData.Context!.ChangeTracker.Entries().Count());
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
