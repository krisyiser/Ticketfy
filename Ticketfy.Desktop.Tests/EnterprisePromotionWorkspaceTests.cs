using Avalonia.Headless.XUnit;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task SeedPromotionWorkspaceAsync(int count = 251)
    {
        await using var db = Db();
        db.Promotions.AddRange(Enumerable.Range(0, count).Select(i => new PromotionEntity
        {
            Id = $"RULE-{i:0000}",
            Name = $"Oferta {i:0000}",
            DiscountValue = 10m,
            StartDate = BusinessTime.Today.AddDays(-1).ToString("s"),
            EndDate = BusinessTime.Today.AddDays(1).ToString("s"),
            TargetProductId = $"OTHER-{i:0000}"
        }));
        await db.SaveChangesAsync();
    }

    [AvaloniaFact]
    public async Task PromotionWorkspace_OnlyMaterializesOneVisiblePage()
    {
        await SeedPromotionWorkspaceAsync();
        var model = new PromotionsViewModel(new PromotionService(new TestDbContextFactory(_options), _actor));
        await model.InitializationTask;
        Assert.Equal(100, model.Promotions.Count);
        var view = new PromotionsView(); var window = Show(view, model);
        try
        {
            var ids = model.Promotions.Select(p => p.Id).ToList();
            await model.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(100, model.Promotions.Count);
            ids.AddRange(model.Promotions.Select(p => p.Id));
            await model.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(51, model.Promotions.Count);
            ids.AddRange(model.Promotions.Select(p => p.Id));
            Assert.Equal(251, ids.Distinct().Count()); Assert.False(model.CanNext);
            await model.PreviousPageCommand.ExecuteAsync(null);
            Assert.Contains("Página 2", model.PageDescription);
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("RULE-0100", model.Promotions[0].Id);
            model.SearchQuery = "RULE-0250";
            Assert.Empty(model.Promotions);
            await model.SearchCommand.ExecuteAsync(null);
            Assert.Equal("RULE-0250", Assert.Single(model.Promotions).Id);
            Assert.False(model.CanPrevious); Assert.False(model.CanNext);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PromotionLegacyLists_RejectExcessWithoutTruncatingCommercialRules()
    {
        await SeedPromotionWorkspaceAsync();
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetActiveAsync());
    }

    [AvaloniaFact]
    public async Task PromotionWorkspace_SavedStatusDoesNotBecomeFailureWhenRefreshFails()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new WorkspacePromotions(new PromotionService(new TestDbContextFactory(_options), _actor));
        var model = new PromotionsViewModel(service);
        await model.LoadPromotionsAsync();
        service.FailAfterWrite = true;
        await model.ToggleStatusCommand.ExecuteAsync(Assert.Single(model.Promotions));
        Assert.False(model.IsFeedbackError);
        Assert.Contains("desactivada", model.FeedbackMessage);
        Assert.Contains("consulta", model.FeedbackMessage);
        await using var db = Db();
        Assert.Equal(0, (await db.Promotions.SingleAsync()).IsActive);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "PROMOTION_INACTIVATED").ToListAsync());
        service.FailReads = false;
        await model.RefreshCommand.ExecuteAsync(null);
        await model.DeletePromotionCommand.ExecuteAsync(Assert.Single(model.Promotions));
        Assert.False(model.IsFeedbackError);
        Assert.Contains("inactivada", model.FeedbackMessage);
        Assert.Contains("consulta", model.FeedbackMessage);
    }

    [Fact]
    public async Task PromotionPages_UseSqlLimitsStableTiesSearchAndAuthorization()
    {
        await SeedPromotionWorkspaceAsync();
        await using var db = Db();
        await db.Promotions.ExecuteUpdateAsync(p => p.SetProperty(x => x.Name, "Mismo nombre"));
        var observer = new PromotionPageQueryObserver();
        var options = new DbContextOptionsBuilder<Ticketfy.Data.AppDbContext>().UseSqlite(_connection).AddInterceptors(observer).Options;
        var service = new PromotionService(new TestDbContextFactory(options), _actor);
        var first = await service.GetPageAsync(0, 200);
        var second = await service.GetPageAsync(1, 200);
        Assert.Equal("RULE-0000", first.Items[0].Id); Assert.True(first.HasMore);
        Assert.Equal(51, second.Items.Count); Assert.Equal("RULE-0200", second.Items[0].Id);
        Assert.False(second.HasMore); Assert.Equal(2, observer.Queries);
        Assert.Equal("RULE-0250", Assert.Single((await service.GetPageAsync(0, search: " rule-0250 ")).Items).Id);
        Assert.Empty((await service.GetPageAsync(0, search: "does-not-exist")).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(0, 201));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetPageAsync(0, 0));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetPageAsync(int.MaxValue));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PromotionService(new TestDbContextFactory(_options)).GetPageAsync(0));
        await db.Promotions.Where(p => p.Id.CompareTo("RULE-0200") >= 0).ExecuteDeleteAsync();
        Assert.Equal(200, (await service.GetAllAsync()).Count);
        Assert.Equal(200, (await service.GetActiveAsync()).Count);
    }

    private sealed class PromotionPageQueryObserver : DbCommandInterceptor
    {
        public int Queries { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"promotions\"", StringComparison.Ordinal))
            {
                ++Queries;
                Assert.Contains("LIMIT", command.CommandText);
                Assert.NotNull(command.Transaction);
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PromotionRules_PricingAndSaleUseWinnerBeyondCatalogLimitAndRealProductCategory()
    {
        await SeedPromotionWorkspaceAsync(1001);
        await using var db = Db();
        var winner = await db.Promotions.SingleAsync(p => p.Id == "RULE-1000");
        winner.TargetProductId = null; winner.TargetCategory = "CAFE"; winner.Priority = -1;
        var product = await db.Products.SingleAsync(p => p.Id == "P"); product.Category = "Cafe";
        await db.SaveChangesAsync();
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var priced = Assert.Single(await service.EvaluateAndApplyPromotionsAsync(
            [new CartItemDto("P", "Producto", 10m, 2) { OriginalUnitPrice = 10m, Category = "Cafe" }]));
        Assert.Equal("RULE-1000", priced.AppliedPromotionId); Assert.Equal(2m, priced.AppliedDiscountAmount);
        var selected = await PromotionRuleReader.ReadApplicableAsync(db,
            [new("P", "Cafe", 2), new("MISSING", "Sin reglas", 1)]);
        Assert.Equal("RULE-1000", Assert.Single(selected).Id);
        var sale = Sale("PAGED-RULE") with
        {
            Total = 18m,
            CashAmount = 18m,
            PaidAmount = 18m,
            Items = [new("P", "Producto", 10m, 0m, 2, "Pza", "Untrusted category", 2m, 18m, 10m, 2m, "RULE-1000")]
        };
        await Sales.SaveAsync(sale);
        await Sales.SaveAsync(sale);
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(18m, (await db.SalePayments.SingleAsync()).Amount);
        Assert.Equal(18d, (await db.Products.AsNoTracking().SingleAsync()).Stock);
        await service.DeleteAsync("RULE-1000", "DISABLE-WINNER");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale with { Id = "EXPIRED-PAGE", OperationId = "EXPIRED-PAGE" }));
        Assert.Empty(await PromotionRuleReader.ReadApplicableAsync(db, []));
    }

    [Fact]
    public async Task PromotionRules_RetainPrecedenceForQuantityCategoryLegacyAndGlobalTargets()
    {
        await using var db = Db();
        PromotionEntity Rule(string id, int priority) => new()
        {
            Id = id,
            Name = id,
            Priority = priority,
            DiscountValue = 10m,
            StartDate = BusinessTime.Today.AddDays(-1).ToString("s"),
            EndDate = BusinessTime.Today.AddDays(1).ToString("s")
        };
        var minimum = Rule("A-MINIMUM", 50); minimum.TargetProductId = "P"; minimum.MinQuantity = 10;
        var category = Rule("B-CATEGORY", 40); category.TargetCategory = "CAFÉ";
        var legacy = Rule("C-LEGACY", 30); legacy.TargetId = "L";
        var tieA = Rule("D-TIE-A", 20); tieA.TargetProductId = "P";
        var tieB = Rule("D-TIE-B", 20); tieB.TargetProductId = "P";
        var global = Rule("Z-GLOBAL", 10);
        db.Promotions.AddRange(minimum, category, legacy, tieB, global, tieA);
        await db.SaveChangesAsync();
        var selected = await PromotionRuleReader.ReadApplicableAsync(db,
            [new("P", "General", 10), new("P", "General", 1), new("X", "café", 1), new("L", null, 1), new("NO-TARGET", null, 1)]);
        Assert.Equal(new[] { minimum.Id, category.Id, legacy.Id, tieA.Id, global.Id }, selected.Select(p => p.Id));
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var cart = await service.EvaluateAndApplyPromotionsAsync([new CartItemDto("P", "P", 10m, 1), new CartItemDto("L", "L", 10m, 1)]);
        Assert.Equal(tieA.Id, cart[0].AppliedPromotionId); Assert.Equal(legacy.Id, cart[1].AppliedPromotionId);
        Assert.Empty(await service.EvaluateAndApplyPromotionsAsync([]));
    }

    [AvaloniaFact]
    public async Task PromotionPages_InvalidLegacyDatesRemainVisibleWithoutInventingValidity()
    {
        await using var db = Db();
        db.Promotions.Add(new PromotionEntity { Id = "BAD-DATE", Name = "Revisar", StartDate = "unknown", EndDate = "unknown" });
        await db.SaveChangesAsync();
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var dto = Assert.Single((await service.GetPageAsync(0)).Items);
        Assert.NotNull(dto.ReadWarning); Assert.Null(dto.StartDate); Assert.Null(dto.EndDate);
        Assert.Contains("Revisar", dto.StatusDisplay);
        Assert.Empty(await service.GetActiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(dto, "INVALID-DATES"));
        var editor = new PromotionDialogViewModel(service, initial: dto);
        Assert.Empty(editor.StartDateText); Assert.Empty(editor.EndDateText);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("vigencia", editor.ErrorMessage);
        Assert.Equal("unknown", (await db.Promotions.AsNoTracking().SingleAsync()).StartDate);
        await service.DeleteAsync(dto.Id, "DISABLE-INVALID"); // Disabling an unverifiable rule must still be possible.
        Assert.Equal(0, (await db.Promotions.AsNoTracking().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task PromotionRules_ReadDatesWithBusinessCalendarAndReplaceLegacyTargetOnlyOnExplicitEdit()
    {
        await using var db = Db();
        var utcNow = DateTimeOffset.UtcNow;
        db.Promotions.AddRange(
            new PromotionEntity { Id = "UTC", Name = "UTC", TargetProductId = "U", StartDate = utcNow.AddHours(-1).ToString("O"), EndDate = utcNow.AddHours(1).ToString("O") },
            new PromotionEntity { Id = "LOCAL", Name = "Local", TargetId = "P", StartDate = BusinessTime.Today.AddDays(-1).ToString("dd/MM/yyyy"), EndDate = BusinessTime.Today.AddDays(1).ToString("dd/MM/yyyy") },
            new PromotionEntity { Id = "ENDED", Name = "Ended", StartDate = BusinessTime.Today.AddDays(-2).ToString("s"), EndDate = BusinessTime.Today.AddDays(-1).ToString("s") },
            new PromotionEntity { Id = "FUTURE", Name = "Future", StartDate = BusinessTime.Today.AddDays(1).ToString("s"), EndDate = BusinessTime.Today.AddDays(2).ToString("s") });
        await db.SaveChangesAsync();
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var active = await service.GetActiveAsync();
        Assert.Equal(new[] { "LOCAL", "UTC" }, active.Select(p => p.Id));
        var legacy = active.Single(p => p.Id == "LOCAL");
        Assert.Equal("P", legacy.TargetProductId);
        await service.SaveAsync(legacy with { TargetProductId = "NEW" }, "REPLACE-LEGACY-TARGET");
        var saved = await db.Promotions.AsNoTracking().SingleAsync(p => p.Id == "LOCAL");
        Assert.Null(saved.TargetId); Assert.Equal("NEW", saved.TargetProductId);
        Assert.Null((await service.EvaluateAndApplyPromotionsAsync([new CartItemDto("P", "P", 10m, 1)])).Single().AppliedPromotionId);
    }

    [AvaloniaFact]
    public async Task PromotionWorkspace_SearchLogoutAndFailureDoNotKeepStalePages()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new WorkspacePromotions(new PromotionService(new TestDbContextFactory(_options), _actor));
        var model = new PromotionsViewModel(service);
        await model.InitializationTask;
        var previous = Assert.Single(model.Promotions);
        var pending = new TaskCompletionSource<CatalogPage<PromotionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NextRead = pending.Task;
        model.SearchQuery = "Other";
        Assert.Empty(model.Promotions);
        var loading = model.SearchCommand.ExecuteAsync(null);
        Assert.True(model.IsLoading); Assert.False(model.CanNext); Assert.False(model.CanEdit);
        model.ClearSessionData();
        pending.SetResult(new([previous], true));
        await loading;
        Assert.Empty(model.Promotions); Assert.False(model.HasLoadedData);
        Assert.False(model.IsLoading); Assert.False(model.CanNext); Assert.Empty(model.FeedbackMessage);
        service.FailReads = true;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(model.Promotions); Assert.True(model.IsFeedbackError); Assert.False(model.HasLoadedData);
        service.FailReads = false;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsFeedbackError); Assert.Empty(model.FeedbackMessage);
    }

    [Fact]
    public async Task PromotionPricing_ConcurrentRuleChangeUsesOneReadSnapshot()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, connection, actor) =>
        {
            var service = new PromotionService(new TestDbContextFactory(options), actor);
            var rule = new PromotionDto("WAL-RULE", "Original", 10m, true,
                StartDate: BusinessTime.Today.AddDays(-1), EndDate: BusinessTime.Today.AddDays(1));
            await service.SaveAsync(rule, "CREATE-WAL");
            rule = Assert.Single(await service.GetAllAsync());
            var interleave = new PromotionSnapshotObserver(() => service.SaveAsync(rule with { DiscountValue = 50m }, "EDIT-WAL"));
            var reading = new DbContextOptionsBuilder<Ticketfy.Data.AppDbContext>().UseSqlite(connection).AddInterceptors(interleave).Options;
            var reader = new PromotionService(new TestDbContextFactory(reading), actor);
            var original = Assert.Single(await reader.EvaluateAndApplyPromotionsAsync([new CartItemDto("P", "P", 10m, 2)]));
            Assert.True(interleave.Committed);
            Assert.Equal(2m, original.AppliedDiscountAmount);
            var after = Assert.Single(await service.EvaluateAndApplyPromotionsAsync([new CartItemDto("P", "P", 10m, 2)]));
            Assert.Equal(10m, after.AppliedDiscountAmount);
        });
    }

    private sealed class PromotionSnapshotObserver(Func<Task> write) : DbCommandInterceptor
    {
        private int _called;
        public bool Committed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM promotions", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _called, 1) == 0)
            {
                Assert.NotNull(command.Transaction);
                await write(); Committed = true;
            }
            return result;
        }
    }

    [AvaloniaFact]
    public async Task PromotionWorkspace_StatusChangeDoesNotOverwriteNewerRuleFromAStalePage()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var model = new PromotionsViewModel(service);
        await model.InitializationTask;
        var stale = Assert.Single(model.Promotions);
        await service.SaveAsync(stale with { Name = "Actualizada", DiscountValue = 25m, TargetProductId = "NEW-TARGET" }, "EDIT-BEFORE-STATUS");
        await model.ToggleStatusCommand.ExecuteAsync(stale);
        await using var db = Db();
        var latest = await db.Promotions.SingleAsync();
        Assert.Equal("Actualizada", latest.Name);
        Assert.Equal(25m, latest.DiscountValue);
        Assert.Equal("NEW-TARGET", latest.TargetProductId);
        Assert.Equal(0, latest.IsActive);
        await model.ToggleStatusCommand.ExecuteAsync(stale); // Repeat the same desired state, not an inverse update.
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "PROMOTION_INACTIVATED").ToListAsync());
    }

    [Fact]
    public async Task PromotionStatus_ValidatesActivationKeepsHistoryAndRejectsMissingOrUnauthorizedChanges()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        await service.SetActiveAsync("RULE-0000", false, "DISABLE-RULE");
        await service.SetActiveAsync("RULE-0000", false, "DISABLE-RULE");
        await service.SetActiveAsync("RULE-0000", true, "ENABLE-RULE");
        await service.SetActiveAsync("RULE-0000", true, "ENABLE-RULE");
        await using var db = Db();
        Assert.Equal(2, await db.TransactionJournal.CountAsync());
        Assert.Equal(1, (await db.Promotions.SingleAsync()).IsActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetActiveAsync("MISSING", true, "MISSING-ENABLE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("MISSING", "MISSING-DELETE"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PromotionService(new TestDbContextFactory(_options)).SetActiveAsync("RULE-0000", false, "UNAUTHORIZED"));
        await db.Promotions.ExecuteUpdateAsync(p => p.SetProperty(x => x.StartDate, "unknown"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetActiveAsync("RULE-0000", true, "INVALID-ENABLE"));
        await service.DeleteAsync("RULE-0000", "DELETE-INVALID");
        Assert.Equal(0, (await db.Promotions.AsNoTracking().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task PromotionWrites_JournalFailureRollsBackRuleAndStatusTogether()
    {
        await SeedPromotionWorkspaceAsync(1);
        var failingOptions = new DbContextOptionsBuilder<Ticketfy.Data.AppDbContext>()
            .UseSqlite(_connection).AddInterceptors(new FailPromotionJournal()).Options;
        var failing = new PromotionService(new TestDbContextFactory(failingOptions), _actor);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var original = Assert.Single(await service.GetAllAsync());
        await Assert.ThrowsAsync<IOException>(() => failing.SetActiveAsync(original.Id, false, "FAILED-STATUS"));
        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(original with { DiscountValue = 45m, Name = "Uncommitted" }, "FAILED-SAVE"));
        Assert.Equal(original, Assert.Single(await service.GetAllAsync()));
        await using var db = Db();
        Assert.Empty(await db.TransactionJournal.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    private sealed class FailPromotionJournal : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TransactionJournalEntryEntity>()
                .Any(e => e.State == EntityState.Added && e.Entity.EventType.StartsWith("PROMOTION_", StringComparison.Ordinal)))
                throw new IOException("Synthetic promotion journal write failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WorkspacePromotions(IPromotionService inner) : IPromotionService
    {
        public bool FailAfterWrite { get; set; }
        public bool FailReads { get; set; }
        public Task<CatalogPage<PromotionDto>>? NextRead { get; set; }
        public Task<List<PromotionDto>> GetAllAsync() => FailReads
            ? Task.FromException<List<PromotionDto>>(new IOException("Synthetic read failure.")) : inner.GetAllAsync();
        public Task<List<PromotionDto>> GetActiveAsync() => inner.GetActiveAsync();
        public Task<CatalogPage<PromotionDto>> GetPageAsync(int page, int size = 100, string? search = null)
        {
            if (NextRead is { } pending) { NextRead = null; return pending; }
            return FailReads ? Task.FromException<CatalogPage<PromotionDto>>(new IOException("Synthetic read failure."))
                : inner.GetPageAsync(page, size, search);
        }
        public Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems) =>
            inner.EvaluateAndApplyPromotionsAsync(cartItems);
        public async Task SaveAsync(PromotionDto promotion, string operationId)
        { await inner.SaveAsync(promotion, operationId); if (FailAfterWrite) FailReads = true; }
        public async Task SetActiveAsync(string id, bool active, string operationId)
        { await inner.SetActiveAsync(id, active, operationId); if (FailAfterWrite) FailReads = true; }
        public async Task DeleteAsync(string id, string operationId)
        { await inner.DeleteAsync(id, operationId); if (FailAfterWrite) FailReads = true; }
    }
}
