using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services;
using Ticketfy.ViewModels.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task PromotionEdit_StaleEditorCannotOverwriteACommittedRule()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var original = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(original with { Name = "Latest", DiscountValue = 20m }, "EDIT-FIRST");
        await Assert.ThrowsAsync<PromotionConflictException>(() => service.SaveAsync(original with { Name = "Stale" }, "EDIT-STALE"));
        var stored = Assert.Single(await service.GetAllAsync());
        Assert.Equal("Latest", stored.Name);
        Assert.Equal(20m, stored.DiscountValue);
    }

    [Fact]
    public async Task PromotionEdit_ReplayedSaveCannotUndoASeparateLaterChange()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var original = new PromotionDto("RETRY-RULE", "Original", 10m, true);
        await service.SaveAsync(original, "CREATE-REPLAY");
        var editing = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(editing with { Name = "Later change", DiscountValue = 30m }, "LATER-EDIT");
        await service.SaveAsync(original, "CREATE-REPLAY");
        var stored = Assert.Single(await service.GetAllAsync());
        Assert.Equal("Later change", stored.Name);
        Assert.Equal(30m, stored.DiscountValue);
        await using var db = Db();
        Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EventType == "PROMOTION_SAVED"));
    }

    [AvaloniaFact]
    public async Task PromotionEditor_NameEditPreservesIntradayScheduleToTheTick()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        DateTime start = BusinessTime.Today.AddHours(13).AddMinutes(17).AddSeconds(8);
        DateTime end = BusinessTime.Today.AddHours(17).AddMinutes(39).AddSeconds(42);
        await service.SaveAsync(new("HOURS", "Scheduled", 10m, true, StartDate: start, EndDate: end), "CREATE-HOURS");
        var before = Assert.Single(await service.GetAllAsync());
        var model = new PromotionDialogViewModel(service, initial: before) { Name = "Renamed only" };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Empty(model.ErrorMessage);
        var after = Assert.Single(await service.GetAllAsync());
        Assert.Equal(before.StartDate, after.StartDate);
        Assert.Equal(before.EndDate, after.EndDate);
    }

    [Fact]
    public async Task PromotionCommands_RejectKeyReuseAndReplayGeneratedIdsWithoutDuplicateDocuments()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var create = new PromotionDto("", "Generated", 10m, true, StartDate: BusinessTime.Today, EndDate: BusinessTime.Today.AddDays(2).AddTicks(1));
        await service.SaveAsync(create, "GENERATED-REPLAY");
        await service.SaveAsync(create, "GENERATED-REPLAY");
        var stored = Assert.Single(await service.GetAllAsync());
        Assert.Equal(0, stored.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(create with { Name = "Different" }, "GENERATED-REPLAY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(create with { EndDate = create.EndDate!.Value.AddTicks(1) }, "GENERATED-REPLAY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetActiveAsync(stored.Id, false, "GENERATED-REPLAY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(create, " "));
        await Assert.ThrowsAsync<PromotionConflictException>(() => service.SaveAsync(create with { Id = stored.Id }, "DUPLICATE-CREATE"));
        await using var db = Db();
        Assert.Single(await db.CommandReceipts.ToListAsync());
        Assert.Single(await db.TransactionJournal.ToListAsync());
        var other = CommercialTestSession.Create(db, false, "OTHER-EDITOR", Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PromotionService(db, other).SaveAsync(create, "GENERATED-REPLAY"));
        Assert.Equal(stored, Assert.Single(await service.GetAllAsync()));
    }

    [Fact]
    public async Task PromotionStatus_ReplaysIncludingNoOpCannotUndoLaterStatusAndInvalidateOpenEditors()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var draft = Assert.Single(await service.GetAllAsync());
        await service.SetActiveAsync(draft.Id, true, "NO-OP-ENABLE");
        await service.DeleteAsync(draft.Id, "DISABLE-FIRST");
        await service.SetActiveAsync(draft.Id, true, "NO-OP-ENABLE");
        Assert.False(Assert.Single(await service.GetAllAsync()).IsActive);
        await service.SetActiveAsync(draft.Id, true, "ENABLE-LATER");
        await service.DeleteAsync(draft.Id, "DISABLE-FIRST");
        var stored = Assert.Single(await service.GetAllAsync());
        Assert.True(stored.IsActive);
        Assert.Equal(2, stored.Revision);
        await Assert.ThrowsAsync<PromotionConflictException>(() => service.SaveAsync(draft with { Name = "Obsolete" }, "STALE-AFTER-STATUS"));
        await using var db = Db();
        Assert.Equal(3, await db.CommandReceipts.CountAsync());
        Assert.Equal(2, await db.TransactionJournal.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionEdit_CategoryChangesInvalidateOpenDrafts(bool remove)
    {
        await using var db = Db();
        db.Categories.Add(new CategoryEntity { Id = "EDIT-CATEGORY", Name = "Original category" });
        db.Promotions.Add(new PromotionEntity { Id = "CATEGORY-RULE", Name = "Category rule", TargetCategory = "Original category", DiscountValue = 10m });
        await db.SaveChangesAsync();
        var service = new PromotionService(db, _actor);
        var draft = Assert.Single(await service.GetAllAsync());
        var categories = new CategoryService(new TestDbContextFactory(_options), _actor);
        if (remove) await categories.DeleteAsync("EDIT-CATEGORY");
        else await categories.SaveAsync("EDIT-CATEGORY", "Current category");
        await Assert.ThrowsAsync<PromotionConflictException>(() => service.SaveAsync(draft with { Name = "Stale editor" }, "STALE-CATEGORY"));
        var saved = Assert.Single(await service.GetAllAsync());
        Assert.Equal(1, saved.Revision);
        Assert.Equal(!remove, saved.IsActive);
        Assert.Equal(remove ? "Original category" : "Current category", saved.TargetCategory);
        Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task PromotionRevision_InvalidStoredVersionCannotWrapOrPartiallyChangeCategory(long revision)
    {
        await SeedPromotionWorkspaceAsync(1);
        await using var db = Db();
        db.Categories.Add(new CategoryEntity { Id = "LIMIT-CATEGORY", Name = "Limit category" });
        await db.SaveChangesAsync();
        await db.Promotions.ExecuteUpdateAsync(p => p.SetProperty(x => x.Revision, revision).SetProperty(x => x.TargetCategory, "Limit category"));
        var service = new PromotionService(db, _actor);
        var draft = Assert.Single(await service.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(draft with { Name = "Overflow" }, "OVERFLOW-SAVE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetActiveAsync(draft.Id, false, "OVERFLOW-STATUS"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CategoryService(new TestDbContextFactory(_options), _actor).SaveAsync("LIMIT-CATEGORY", "Changed"));
        Assert.Equal(draft, Assert.Single(await service.GetAllAsync()));
        Assert.Equal("Limit category", (await db.Categories.AsNoTracking().SingleAsync()).Name);
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task PromotionRevision_EfRejectsStaleTrackedEntityAndMissingRuleCannotBeResurrected()
    {
        await SeedPromotionWorkspaceAsync(1);
        await using var first = Db();
        await using var second = Db();
        var a = await first.Promotions.SingleAsync();
        var b = await second.Promotions.SingleAsync();
        a.Name = "Winner"; a.Revision++;
        await first.SaveChangesAsync();
        b.Name = "Stale"; b.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        var service = new PromotionService(first, _actor);
        var draft = Assert.Single(await service.GetAllAsync());
        await first.Promotions.ExecuteDeleteAsync(); // Synthetic missing-record failure, never a production delete command.
        await Assert.ThrowsAsync<PromotionConflictException>(() => service.SaveAsync(draft, "MISSING-EDIT"));
        Assert.Empty(await first.Promotions.AsNoTracking().ToListAsync());
        Assert.Empty(await first.CommandReceipts.ToListAsync());
    }

    [Fact]
    public async Task PromotionRevision_ConcurrentEditorsHaveOneWinnerWithMatchingAuditAfterReopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ticketfy-promotion-conflict-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            CommercialOperationContext firstActor, secondActor;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                firstActor = CommercialTestSession.Create(db, false, "EDITOR-A");
                secondActor = CommercialTestSession.Create(db, false, "EDITOR-B", Guid.NewGuid());
                db.Promotions.Add(new PromotionEntity { Id = "CONCURRENT", Name = "Original", DiscountValue = 10m });
                await db.SaveChangesAsync();
            }
            var factory = new TestDbContextFactory(options);
            var draft = Assert.Single(await new PromotionService(factory, firstActor).GetAllAsync());
            var actors = new[] { firstActor, secondActor };
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                try
                {
                    await new PromotionService(factory, actors[i]).SaveAsync(draft with { Name = $"Editor {i}", DiscountValue = 20m + i }, $"EDIT-{i}");
                    return true;
                }
                catch (PromotionConflictException) { return false; }
            })));
            Assert.Single(results, result => result);
            int winner = Array.IndexOf(results, true);
            await using var reopened = new AppDbContext(options);
            var stored = await reopened.Promotions.SingleAsync();
            Assert.Equal($"Editor {winner}", stored.Name);
            Assert.Equal(20m + winner, stored.DiscountValue);
            Assert.Equal(1, stored.Revision);
            var receipt = Assert.Single(await reopened.CommandReceipts.ToListAsync());
            var journal = Assert.Single(await reopened.TransactionJournal.ToListAsync());
            Assert.Equal($"EDIT-{winner}", receipt.OperationId);
            Assert.Equal(receipt.OperationId, journal.OperationId);
            Assert.Equal(receipt.CashierUserId, journal.CashierUserId);
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task PromotionRevision_MigrationPreservesLegacyRuleAndCannotResetRevisionOnReplayOrDowngrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020075036_EnforcePasswordPolicyTransition");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO promotions (id,name,type,strategy_type,targetId,target_category,discountValue,min_quantity,free_quantity,buyQty,payQty,start_date,end_date,isActive,priority)
            VALUES ('LEGACY-REV','Legacy rule','product',0,'P','',1000,1.25,0,0,0,'2030-01-01T12:34:56.1234567-06:00','2030-01-02T13:45:56.7654321-06:00',1,7);
            """);
        await migrator.MigrateAsync();
        var rule = await db.Promotions.AsNoTracking().SingleAsync();
        Assert.Equal(0, rule.Revision);
        Assert.Equal(10m, rule.DiscountValue);
        Assert.Equal("P", rule.TargetId);
        Assert.Equal(1.25d, rule.MinQuantity);
        var actor = CommercialTestSession.Create(db, false);
        var service = new PromotionService(db, actor);
        var draft = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(draft with { Name = "Explicit edit" }, "LEGACY-REV-EDIT");
        await migrator.MigrateAsync();
        var updated = await db.Promotions.AsNoTracking().SingleAsync();
        Assert.Equal(1, updated.Revision);
        Assert.Equal(rule.StartDate, updated.StartDate);
        Assert.Equal(rule.EndDate, updated.EndDate);
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261020075036_EnforcePasswordPolicyTransition"));
        await service.SaveAsync(draft with { Name = "Explicit edit" }, "LEGACY-REV-EDIT");
        Assert.Equal(1, (await db.Promotions.AsNoTracking().SingleAsync()).Revision);
        Assert.Single(await db.CommandReceipts.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
