using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseCommercialDirectorySnapshotTests
{
    [Theory]
    [InlineData("wallet")]
    [InlineData("components")]
    [InlineData("legacy-kits")]
    [InlineData("barcode")]
    public async Task DirectoryReads_KeepHeadersRowsAndTotalsInOneSnapshot(string kind)
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, connection, actor) =>
        {
            await using (var setup = new AppDbContext(options))
            {
                setup.Products.AddRange(new ProductEntity { Id = "P", Name = "Componente" },
                    new ProductEntity { Id = "K", Barcode = "K", Name = "Combo", Price = 10m, IsKit = true });
                setup.ItemKits.Add(new ItemKitEntity { Id = "K", ParentProductId = "K", KitBarcode = "K", Name = "Combo", Price = 10m });
                setup.ItemKitItems.Add(new ItemKitItemEntity { Id = "L", ItemKitId = "K", ProductId = "P", Quantity = 1 });
                setup.Giftcards.AddRange(new GiftcardEntity { Id = "A", CardNumber = "A", Balance = .01m },
                    new GiftcardEntity { Id = "B", CardNumber = "B", Balance = 2.01m });
                await setup.SaveChangesAsync();
            }
            var observer = new DirectorySnapshotWrite(kind == "wallet" ? "giftcards" : "item_kits", async () =>
            {
                await using var writer = new AppDbContext(options);
                (await writer.Products.SingleAsync(p => p.Id == "K")).Price = 99m;
                (await writer.ItemKitItems.SingleAsync()).Quantity = 2;
                writer.ItemKitItems.Add(new ItemKitItemEntity { Id = "L2", ItemKitId = "K", ProductId = "P", Quantity = 3 });
                (await writer.Giftcards.SingleAsync(g => g.Id == "A")).Balance = 999.99m;
                writer.Giftcards.Add(new GiftcardEntity { Id = "C", CardNumber = "C", Balance = 100m });
                await writer.SaveChangesAsync();
            });
            var readOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options;
            var factory = new TestDbContextFactory(readOptions);
            var kits = new ItemKitService(factory, actor);
            if (kind == "wallet")
            {
                var result = await new GiftcardService(factory, actor).GetPageAsync(0, 1);
                Assert.Equal(2, result.TotalCount); Assert.Equal(2.02m, result.TotalRecordedBalance);
                Assert.Equal(.01m, Assert.Single(result.Items).RecordedBalance); Assert.True(result.HasMore);
                var next = await new GiftcardService(new TestDbContextFactory(options), actor).GetPageAsync(0);
                Assert.Equal(3, next.TotalCount); Assert.Equal(1102m, next.TotalRecordedBalance);
            }
            else if (kind == "components")
            {
                var result = await kits.GetComponentsPageAsync("K", 0);
                Assert.Equal(10m, result!.Kit.Price); Assert.Equal(1, result.Kit.ComponentCount);
                Assert.Equal(1, Assert.Single(result.Items).Quantity); Assert.False(result.HasMore);
            }
            else
            {
                var result = kind == "barcode" ? await kits.GetByBarcodeAsync("K") : Assert.Single(await kits.GetAllAsync());
                Assert.Equal(10m, result!.Price); Assert.Equal(1, Assert.Single(result.Items).Quantity);
            }
            Assert.True(observer.Committed);
            var current = await new ItemKitService(new TestDbContextFactory(options), actor).GetComponentsPageAsync("K", 0);
            Assert.Equal(99m, current!.Kit.Price); Assert.Equal(2, current.Kit.ComponentCount); Assert.Equal(2, current.Items.Count);
        });
    }

    private sealed class DirectorySnapshotWrite(string table, Func<Task> write) : DbCommandInterceptor
    {
        private int _once;
        public bool Committed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _once, 1) == 0)
            { Assert.NotNull(command.Transaction); await write(); Committed = true; }
            return result;
        }
    }
}
