using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task SeedCommercialDirectoriesAsync(int count)
    {
        await using var db = Db();
        for (int i = 0; i < count; i++)
        {
            string id = $"KIT-{i:0000}";
            db.Products.Add(new ProductEntity { Id = id, Barcode = id, Name = $"Combo {i:0000}", IsKit = true, Price = 3.01m });
            db.ItemKits.Add(new ItemKitEntity { Id = id, ParentProductId = id, KitBarcode = id, Name = $"Combo {i:0000}", Price = 3.01m });
            db.ItemKitItems.Add(new ItemKitItemEntity { Id = $"PART-{i:0000}", ItemKitId = id, ProductId = "P", Quantity = 2 });
            db.Giftcards.Add(new GiftcardEntity { Id = $"CARD-{i:0000}", CardNumber = $"CARD-{i:0000}", CustomerId = "C", Balance = .01m });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CommercialDirectories_PagesFindAllRowsAndWalletTotalsNeverBecomePageTotals()
    {
        await SeedCommercialDirectoriesAsync(251);
        var factory = new TestDbContextFactory(_options);
        var kits = new ItemKitService(factory, _actor);
        var wallets = new GiftcardService(factory, _actor);
        var kitIds = new List<string>(); var walletIds = new List<string>();
        for (int page = 0; page < 3; page++)
        {
            var kp = await kits.GetPageAsync(page);
            var wp = await wallets.GetPageAsync(page);
            Assert.Equal(page < 2, kp.HasMore); Assert.Equal(page < 2, wp.HasMore);
            Assert.Equal(102.51m, wp.TotalRecordedBalance); Assert.Equal(252, wp.TotalCount);
            kitIds.AddRange(kp.Items.Select(k => k.Id)); walletIds.AddRange(wp.Items.Select(w => w.Id));
            Assert.All(kp.Items, k => { Assert.Equal(1, k.ComponentCount); Assert.False(k.HasCatalogMismatch); });
        }
        Assert.Equal(251, kitIds.Distinct().Count()); Assert.Equal(252, walletIds.Distinct().Count());
        Assert.Equal("KIT-0250", Assert.Single((await kits.GetPageAsync(0, search: " kit-0250 ")).Items).Id);
        var filtered = await wallets.GetPageAsync(0, search: " card-0250 ");
        Assert.Equal("CARD-0250", Assert.Single(filtered.Items).Id); Assert.Equal(.01m, filtered.TotalRecordedBalance);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Empty((await kits.GetPageAsync(3)).Items); Assert.Empty((await wallets.GetPageAsync(3)).Items);
        Assert.Empty((await wallets.GetPageAsync(0, search: "missing")).Items);
        Assert.Equal(0m, (await wallets.GetPageAsync(0, search: "missing")).TotalRecordedBalance);
    }

    [Fact]
    public async Task CommercialDirectories_LegacyReadsAreCompleteAtLimitAndRejectExcessBeforeLoadingRecipes()
    {
        await SeedCommercialDirectoriesAsync(199);
        var factory = new TestDbContextFactory(_options);
        var kits = new ItemKitService(factory, _actor); var wallets = new GiftcardService(factory, _actor);
        Assert.Equal(199, (await kits.GetAllAsync()).Count);
        Assert.Equal(200, (await wallets.GetAllAsync()).Count);
        await using var db = Db();
        db.Giftcards.Add(new GiftcardEntity { Id = "EXCESS", CardNumber = "EXCESS", Balance = 1 });
        for (int i = 199; i < 201; i++)
        {
            string id = $"KIT-{i:0000}";
            db.Products.Add(new ProductEntity { Id = id, Name = id, Barcode = id, IsKit = true });
            db.ItemKits.Add(new ItemKitEntity { Id = id, ParentProductId = id, KitBarcode = id, Name = id });
        }
        await db.SaveChangesAsync();
        var observer = new CommercialDirectoryQueryObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemKitService(new TestDbContextFactory(options), _actor).GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GiftcardService(new TestDbContextFactory(options), _actor).GetAllAsync());
        Assert.Equal(0, observer.ComponentDetails);
    }

    [Fact]
    public async Task KitDirectory_ComponentsArePagedAndOversizedLegacyRecipesAreNeverTruncated()
    {
        await SeedCommercialDirectoriesAsync(1);
        await using var db = Db();
        db.ItemKitItems.AddRange(Enumerable.Range(1, 250).Select(i => new ItemKitItemEntity
        { Id = $"PART-{i:0000}", ItemKitId = "KIT-0000", ProductId = "P", Quantity = i }));
        await db.SaveChangesAsync();
        var observer = new CommercialDirectoryQueryObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new ItemKitService(new TestDbContextFactory(options), _actor);
        Assert.Equal(251, Assert.Single((await service.GetPageAsync(0)).Items).ComponentCount);
        Assert.Equal(0, observer.ComponentDetails);
        var seen = new List<string>();
        for (int page = 0; page < 3; page++)
        {
            var result = await service.GetComponentsPageAsync("KIT-0000", page);
            Assert.NotNull(result); Assert.Equal(251, result.Kit.ComponentCount);
            Assert.Equal(page < 2, result.HasMore); seen.AddRange(result.Items.Select(i => i.Id));
        }
        Assert.Equal(251, seen.Distinct().Count()); Assert.Equal(3, observer.ComponentDetails);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetByBarcodeAsync("KIT-0000"));
        Assert.Null(await service.GetComponentsPageAsync("missing", 0));
        Assert.Null(await service.GetByBarcodeAsync(""));
        Assert.Null(await service.GetByBarcodeAsync("missing"));
    }

    [Fact]
    public async Task CommercialDirectories_KeepInactiveRecordsDiscoverableAndUsePosMasterForKitPriceAndName()
    {
        await SeedCommercialDirectoriesAsync(2);
        await using var db = Db();
        var product = await db.Products.SingleAsync(p => p.Id == "KIT-0000");
        product.Name = "Nombre del POS"; product.Price = 19.99m; product.Barcode = "NEW-CODE";
        (await db.Products.SingleAsync(p => p.Id == "KIT-0001")).IsActive = false;
        (await db.Giftcards.SingleAsync(g => g.Id == "G")).IsActive = false;
        await db.SaveChangesAsync();
        var kits = new ItemKitService(db, _actor); var wallets = new GiftcardService(db, _actor);
        var item = Assert.Single((await kits.GetPageAsync(0)).Items);
        Assert.Equal("Nombre del POS", item.Name); Assert.Equal(19.99m, item.Price); Assert.True(item.HasCatalogMismatch);
        Assert.Equal(19.99m, (await kits.GetByBarcodeAsync("NEW-CODE"))!.Price);
        Assert.Null(await kits.GetByBarcodeAsync("KIT-0000"));
        Assert.Equal(2, (await kits.GetPageAsync(0, includeInactive: true)).Items.Count);
        Assert.False((await kits.GetComponentsPageAsync("KIT-0001", 0))!.Kit.IsActive);
        Assert.Equal(.02m, (await wallets.GetPageAsync(0)).TotalRecordedBalance);
        Assert.Equal(100.02m, (await wallets.GetPageAsync(0, includeInactive: true)).TotalRecordedBalance);
    }

    [Fact]
    public async Task CommercialDirectories_RejectInvalidPagingAndRequireAuthorization()
    {
        var factory = new TestDbContextFactory(_options);
        var kits = new ItemKitService(factory, _actor); var wallets = new GiftcardService(factory, _actor);
        foreach (var (page, size) in new[] { (-1, 100), (0, 0), (0, 201) })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => kits.GetPageAsync(page, size));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => kits.GetComponentsPageAsync("K", page, size));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wallets.GetPageAsync(page, size));
        }
        await Assert.ThrowsAsync<OverflowException>(() => kits.GetPageAsync(int.MaxValue));
        await Assert.ThrowsAsync<OverflowException>(() => kits.GetComponentsPageAsync("K", int.MaxValue));
        await Assert.ThrowsAsync<OverflowException>(() => wallets.GetPageAsync(int.MaxValue));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ItemKitService(factory).GetPageAsync(0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ItemKitService(factory).GetComponentsPageAsync("K", 0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GiftcardService(factory).GetPageAsync(0));
    }

    [Fact]
    public async Task CommercialDirectories_VolumeLoadsOnlyVisibleRowsAndStreamsExactFullWalletBalance()
    {
        await SeedCommercialDirectoriesAsync(10001);
        var observer = new CommercialDirectoryQueryObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var factory = new TestDbContextFactory(options);
        var kits = await new ItemKitService(factory, _actor).GetPageAsync(0);
        var wallets = await new GiftcardService(factory, _actor).GetPageAsync(0);
        Assert.Equal(100, kits.Items.Count); Assert.Equal(100, wallets.Items.Count);
        Assert.True(kits.HasMore); Assert.True(wallets.HasMore);
        Assert.Equal(10002, wallets.TotalCount); Assert.Equal(200.01m, wallets.TotalRecordedBalance);
        Assert.Equal(0, observer.ComponentDetails); Assert.Equal(1, observer.WalletDetails); Assert.Equal(1, observer.WalletScalarQueries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KitReadFailure_IsNotReportedAsAnEmptyDirectoryOrUnknownBarcode(bool barcode)
    {
        var observer = new CommercialDirectoryQueryObserver { FailKitReads = true };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new ItemKitService(new TestDbContextFactory(options), _actor);
        await Assert.ThrowsAsync<IOException>(async () => { if (barcode) await service.GetByBarcodeAsync("KIT"); else await service.GetAllAsync(); });
    }

    [Fact]
    public async Task KitDirectory_DoesNotHideOrphanedLegacyComponentsOrPickAnAmbiguousRecipe()
    {
        await SeedCommercialDirectoriesAsync(1);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO item_kits (id,parent_product_id,kit_barcode,name,price,description,created_at)
                VALUES ('ORPHAN','MISSING','OLD','Huérfano',100,'Pendiente de conciliación','');
                INSERT INTO item_kit_items (id,item_kit_id,product_id,quantity) VALUES ('MISSING-PART','ORPHAN','MISSING',2);
                """);
        }
        finally { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;"); }
        var service = new ItemKitService(db, _actor);
        var orphan = Assert.Single((await service.GetPageAsync(0, search: "ORPHAN", includeInactive: true)).Items);
        Assert.False(orphan.IsActive); Assert.True(orphan.HasCatalogMismatch);
        var recipe = await service.GetComponentsPageAsync(orphan.Id, 0);
        Assert.Equal("Producto no encontrado", Assert.Single(recipe!.Items).ProductName);
        db.ItemKits.Add(new ItemKitEntity { Id = "DUPLICATE", ParentProductId = "KIT-0000", KitBarcode = "DUPLICATE", Name = "Duplicado" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetByBarcodeAsync("KIT-0000"));
    }

    private sealed class CommercialDirectoryQueryObserver : DbCommandInterceptor
    {
        public bool FailKitReads { get; set; }
        public int ComponentDetails { get; private set; }
        public int WalletDetails { get; private set; }
        public int WalletScalarQueries { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (FailKitReads && command.CommandText.Contains("FROM \"item_kits\"", StringComparison.Ordinal)) throw new IOException("Synthetic read failure");
            if (command.CommandText.Contains("FROM \"giftcards\"", StringComparison.Ordinal))
            {
                Assert.NotNull(command.Transaction);
                if (command.CommandText.Contains("\"card_number\"", StringComparison.Ordinal))
                { WalletDetails++; Assert.Contains("LIMIT", command.CommandText); }
                else
                {
                    WalletScalarQueries++;
                    Assert.StartsWith("SELECT \"g\".\"balance\"", command.CommandText, StringComparison.Ordinal);
                    Assert.DoesNotContain("customer_id", command.CommandText);
                }
            }
            if (command.CommandText.Contains("FROM \"item_kit_items\"", StringComparison.Ordinal)
                && !command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
            {
                ComponentDetails++; Assert.Contains("LIMIT", command.CommandText); Assert.NotNull(command.Transaction);
            }
            return ValueTask.FromResult(result);
        }
    }
}
