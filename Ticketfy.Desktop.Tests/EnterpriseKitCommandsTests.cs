using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private ItemKitService KitsService() => new(new TestDbContextFactory(_options), _actor);
    private static KitDraft NewKit(string id = "EDIT-KIT") =>
        new(id, id + "-CODE", "Combo", 10m, "Descripción", [new("", "", "P", "Producto", 1)]);

    [Fact]
    public async Task KitCommands_ConcurrentDraftsAndReplaysPreserveTheLatestRecipeAndExactAudit()
    {
        var service = KitsService();
        var create = NewKit();
        await service.SaveAsync(create, "CREATE");
        var original = (await service.GetForEditAsync(create.ProductId))!;
        var update = original with
        {
            Name = "Latest",
            Price = 20.12m,
            Description = "Updated",
            Items = [original.Items.Single() with { Quantity = 2 }]
        };
        await service.SaveAsync(update, "UPDATE");
        await Assert.ThrowsAsync<KitConflictException>(() => service.SaveAsync(original with { Name = "Stale" }, "STALE"));
        await service.SaveAsync(create, "CREATE");
        await service.SaveAsync(update, "UPDATE");
        var current = (await service.GetForEditAsync(create.ProductId))!;
        Assert.Equal("Latest", current.Name); Assert.Equal(20.12m, current.Price);
        Assert.Equal(1, current.Revision); Assert.Equal(2, current.Items.Single().Quantity);
        await using var db = Db();
        Assert.Equal(2, await db.CommandReceipts.CountAsync());
        var journals = await db.TransactionJournal.OrderBy(j => j.OperationId).ToListAsync();
        Assert.Equal(2, journals.Count);
        var journal = journals.Single(j => j.OperationId == "UPDATE");
        Assert.Equal(CommercialTestSession.UserId.ToString(), journal.CashierUserId);
        Assert.Equal("TEST-REGISTER", journal.RegisterId); Assert.Equal("SHIFT-1", journal.ShiftId);
        using var details = JsonDocument.Parse(journal.Details);
        Assert.Equal("Combo", details.RootElement.GetProperty("Before").GetProperty("Name").GetString());
        Assert.Equal(1d, details.RootElement.GetProperty("Before").GetProperty("Items")[0].GetProperty("Quantity").GetDouble());
        Assert.Equal(2d, details.RootElement.GetProperty("After").GetProperty("Items")[0].GetProperty("Quantity").GetDouble());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task KitCommands_OperationIdentityBindsActorPayloadRevisionAndCommand()
    {
        var service = KitsService(); var draft = NewKit();
        await service.SaveAsync(draft, "BOUND");
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(draft with { Price = 11m }, "BOUND"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(
            draft with { Items = [draft.Items[0] with { Quantity = 2 }] }, "BOUND"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SetActiveAsync(draft.ProductId, false, "BOUND"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(draft, " "));
        await Assert.ThrowsAsync<KitConflictException>(() => service.SaveAsync(draft, "DUPLICATE"));
        await using var db = Db();
        var other = CommercialTestSession.Create(db, true, "OTHER", Guid.NewGuid());
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => new ItemKitService(db, other).SaveAsync(draft, "BOUND"));
        Assert.Single(await db.CommandReceipts.ToListAsync());
        Assert.Single(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task KitCommands_StatusNoOpIsDurableAndEditingInactiveDoesNotReactivateOrResetCatalog()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var original = (await service.GetForEditAsync("EDIT-KIT"))!;
        await service.SetActiveAsync("EDIT-KIT", true, "NOOP");
        await service.SetActiveAsync("EDIT-KIT", false, "OFF");
        await service.SetActiveAsync("EDIT-KIT", true, "NOOP");
        await Assert.ThrowsAsync<KitConflictException>(() => service.SaveAsync(original, "STALE"));
        await using (var db = Db())
        {
            var product = await db.Products.SingleAsync(p => p.Id == "EDIT-KIT");
            product.Category = "Combos especiales"; product.LocationRack = "Virtual"; product.TaxRate = 0m;
            await db.SaveChangesAsync();
        }
        var inactive = (await service.GetForEditAsync("EDIT-KIT"))!;
        Assert.False(inactive.IsActive);
        await service.SaveAsync(inactive with { Name = "Repaired", Items = [inactive.Items[0] with { Quantity = 2 }] }, "REPAIR");
        Assert.False((await service.GetForEditAsync("EDIT-KIT"))!.IsActive);
        await service.SetActiveAsync("EDIT-KIT", true, "ON");
        await service.SetActiveAsync("EDIT-KIT", false, "OFF");
        await using var verify = Db();
        var stored = await verify.Products.SingleAsync(p => p.Id == "EDIT-KIT");
        Assert.True(stored.IsActive); Assert.Equal(3, stored.StockVersion);
        Assert.Equal("Combos especiales", stored.Category); Assert.Equal("Virtual", stored.LocationRack); Assert.Equal(0m, stored.TaxRate);
        Assert.Equal(5, await verify.CommandReceipts.CountAsync());
        Assert.Equal(4, await verify.TransactionJournal.CountAsync());
    }

    [Fact]
    public async Task KitCommands_ActivationRejectsInvalidIngredientsAndMissingOrAmbiguousEditingNeverTruncates()
    {
        await SeedGraphKitAsync("BROKEN", ("P", .5));
        await using (var db = Db())
        {
            (await db.Products.SingleAsync(p => p.Id == "BROKEN")).IsActive = false;
            await db.SaveChangesAsync();
        }
        var service = KitsService();
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SetActiveAsync("BROKEN", true, "INVALID-ON"));
        Assert.False((await service.GetForEditAsync("BROKEN"))!.IsActive);
        Assert.Null(await service.GetForEditAsync("MISSING"));
        Assert.Null(await service.GetForEditAsync("P"));
        var draft = (await service.GetForEditAsync("BROKEN"))!;
        await service.SaveAsync(draft with { Items = [draft.Items[0] with { Quantity = 1 }] }, "FIX");
        await service.SetActiveAsync("BROKEN", true, "ON");
        await using var verify = Db();
        verify.ItemKitItems.AddRange(Enumerable.Range(0, 200).Select(i => new ItemKitItemEntity
        { Id = "OVER-" + i, ItemKitId = "BROKEN-RECIPE", ProductId = "P", Quantity = 1 }));
        await verify.SaveChangesAsync();
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.GetForEditAsync("BROKEN"));
        var beforeCount = await verify.CommandReceipts.CountAsync();
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(
            NewKit("BROKEN") with { Revision = 2 }, "OVERSIZED"));
        Assert.Equal(beforeCount, await verify.CommandReceipts.CountAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task KitCommands_InvalidRevisionCannotWrap(long revision)
    {
        await SeedGraphKitAsync("REV", ("P", 1));
        await using var db = Db();
        await db.Products.Where(p => p.Id == "REV").ExecuteUpdateAsync(p => p.SetProperty(x => x.StockVersion, revision));
        var service = KitsService(); var draft = (await service.GetForEditAsync("REV"))!;
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(draft with { Name = "Overflow" }, "EDIT"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SetActiveAsync("REV", false, "OFF"));
        Assert.Empty(await db.CommandReceipts.ToListAsync()); Assert.Empty(await db.TransactionJournal.ToListAsync());
        Assert.Equal(draft.Name, (await service.GetForEditAsync("REV"))!.Name);
    }

    [Fact]
    public async Task KitCommands_PermissionsAreEnforcedAtEveryReadAndWriteAndRequireOwnedShift()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var draft = (await service.GetForEditAsync("EDIT-KIT"))!;
        await using var db = Db();
        var cashier = CommercialTestSession.Create(db, true, "CASHIER", Guid.NewGuid(), "CAJERO");
        var restricted = new ItemKitService(db, cashier);
        var caps = await restricted.GetCapabilitiesAsync();
        Assert.Equal(new KitCapabilities(false, false, false), caps);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.GetForEditAsync("EDIT-KIT"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.SaveAsync(NewKit("UNAUTHORIZED"), "DENIED-CREATE"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.SaveAsync(draft, "DENIED-EDIT"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.SetActiveAsync("EDIT-KIT", false, "DENIED-OFF"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.SetActiveAsync("EDIT-KIT", true, "DENIED-ON"));
        var noShift = new ItemKitService(db, CommercialTestSession.Create(db, false, "NO-SHIFT", Guid.NewGuid()));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => noShift.SaveAsync(NewKit("NO-SHIFT"), "NO-SHIFT"));
        Assert.Single(await db.CommandReceipts.ToListAsync());
    }

    [Fact]
    public async Task KitCommands_AuditFailureRollsBackProvisionalRecipeReceiptAndMaster()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var original = (await service.GetForEditAsync("EDIT-KIT"))!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailKitJournal()).Options;
        var failing = new ItemKitService(new TestDbContextFactory(options), _actor);
        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(original with
        {
            Name = "Uncommitted",
            Items = [original.Items[0] with { Quantity = 3 }]
        }, "FAIL"));
        var stored = (await service.GetForEditAsync("EDIT-KIT"))!;
        Assert.Equal(original.Name, stored.Name); Assert.Equal(original.Revision, stored.Revision);
        Assert.Equal(original.Items, stored.Items);
        await using var db = Db();
        Assert.Single(await db.CommandReceipts.ToListAsync()); Assert.Single(await db.TransactionJournal.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KitCommands_LostCommitResponseIsNotReportedAsRejectionAndRetryNeverDuplicates(bool invalidOperation)
    {
        var interceptor = new LoseKitCommitResponse(invalidOperation);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(interceptor).Options;
        var service = new ItemKitService(new TestDbContextFactory(options), _actor);
        var draft = NewKit();
        var error = await Record.ExceptionAsync(() => service.SaveAsync(draft, "LOST"));
        Assert.NotNull(error); Assert.IsNotType<KitCommandRejectedException>(error);
        Assert.Equal(invalidOperation ? typeof(InvalidOperationException) : typeof(IOException), error.GetType());
        await service.SaveAsync(draft, "LOST");
        await using var db = Db();
        Assert.Single(await db.ItemKits.ToListAsync()); Assert.Single(await db.ItemKitItems.ToListAsync());
        Assert.Single(await db.TransactionJournal.ToListAsync()); Assert.Single(await db.CommandReceipts.ToListAsync());
    }

    private sealed class FailKitJournal : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TransactionJournalEntryEntity>().Any(e => e.Entity.EventType == "KIT_SAVED"))
                throw new IOException("Synthetic audit write failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task KitCommands_MissingOrAmbiguousRecipesAndDeletedDraftsCannotBeRecreatedImplicitly()
    {
        await SeedGraphKitAsync("AMBIGUOUS", ("P", 1));
        var service = KitsService(); var draft = (await service.GetForEditAsync("AMBIGUOUS"))!;
        await using var db = Db();
        db.ItemKits.Add(new ItemKitEntity { Id = "SECOND-RECIPE", ParentProductId = "AMBIGUOUS", Name = "Second" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.GetForEditAsync("AMBIGUOUS"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(draft, "AMBIGUOUS"));
        await db.ItemKits.Where(k => k.Id == "SECOND-RECIPE").ExecuteDeleteAsync();
        await db.ItemKitItems.ExecuteDeleteAsync(); await db.ItemKits.ExecuteDeleteAsync();
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.GetForEditAsync("AMBIGUOUS"));
        await db.Products.Where(p => p.Id == "AMBIGUOUS").ExecuteDeleteAsync();
        await Assert.ThrowsAsync<KitConflictException>(() => service.SaveAsync(draft, "MISSING-DRAFT"));
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => service.SaveAsync(null!, "NULL"));
        Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("code")]
    [InlineData("description")]
    [InlineData("quantity")]
    [InlineData("activation")]
    public async Task KitCommands_RejectOversizedInputAndActivationOutsideExplicitStatusCommand(string invalid)
    {
        var draft = NewKit();
        draft = invalid switch
        {
            "name" => draft with { Name = new string('n', 201) },
            "code" => draft with { Barcode = new string('b', 129) },
            "description" => draft with { Description = new string('d', 2001) },
            "quantity" => draft with { Items = [draft.Items[0] with { Quantity = double.PositiveInfinity }] },
            _ => draft with { IsActive = false }
        };
        await Assert.ThrowsAsync<KitCommandRejectedException>(() => KitsService().SaveAsync(draft, "INVALID"));
        await using var db = Db();
        Assert.Empty(await db.CommandReceipts.ToListAsync()); Assert.Empty(await db.ItemKits.ToListAsync());
    }

    [Fact]
    public async Task KitCommands_ConcurrentConnectionsCommitExactlyOneRevisionAndAudit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-kit-edit-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            CommercialOperationContext firstActor, secondActor;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync(); await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                firstActor = CommercialTestSession.Create(db, true, "EDITOR-A");
                secondActor = CommercialTestSession.Create(db, true, "EDITOR-B", Guid.NewGuid());
                db.Products.Add(new ProductEntity { Id = "P", Name = "Physical", Price = 10m, Cost = 3m, Stock = 20 });
                await db.SaveChangesAsync();
            }
            var factory = new TestDbContextFactory(options);
            var service = new ItemKitService(factory, firstActor);
            await service.SaveAsync(NewKit(), "CREATE");
            var draft = (await service.GetForEditAsync("EDIT-KIT"))!;
            var actors = new[] { firstActor, secondActor };
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                try
                {
                    await new ItemKitService(factory, actors[i]).SaveAsync(draft with
                    {
                        Name = $"Editor {i}",
                        Items = [draft.Items[0] with { Quantity = i + 2 }]
                    }, $"EDIT-{i}");
                    return true;
                }
                catch (KitConflictException) { return false; }
            })));
            Assert.Single(results, value => value); int winner = Array.IndexOf(results, true);
            await using var reopened = new AppDbContext(options);
            var product = await reopened.Products.SingleAsync(p => p.Id == "EDIT-KIT");
            Assert.Equal($"Editor {winner}", product.Name); Assert.Equal(1, product.StockVersion);
            Assert.Equal(winner + 2, (await reopened.ItemKitItems.SingleAsync()).Quantity);
            Assert.Equal(2, await reopened.CommandReceipts.CountAsync());
            var receipt = await reopened.CommandReceipts.SingleAsync(c => c.OperationId == $"EDIT-{winner}");
            var journal = await reopened.TransactionJournal.SingleAsync(c => c.OperationId == receipt.OperationId);
            Assert.Equal(receipt.CashierUserId, journal.CashierUserId);
            Assert.Equal(receipt.RegisterId, journal.RegisterId); Assert.Equal(receipt.ShiftId, journal.ShiftId);
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private sealed class LoseKitCommitResponse(bool invalidOperation) : DbTransactionInterceptor
    {
        private bool _thrown;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_thrown) return Task.CompletedTask;
            _thrown = true;
            throw invalidOperation ? new InvalidOperationException("Synthetic lost commit acknowledgment") : new IOException("Synthetic lost commit acknowledgment");
        }
    }
}
