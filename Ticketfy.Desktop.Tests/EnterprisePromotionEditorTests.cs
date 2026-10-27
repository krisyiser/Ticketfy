using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task PromotionEditor_TwoOpenDraftsPreserveWinnerAndRequireExplicitReopeningAfterConflict()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        await service.SaveAsync(new("TWO-EDITORS", "Original", 10m, true), "CREATE-EDITORS");
        var rule = Assert.Single(await service.GetAllAsync());
        var first = new PromotionDialogViewModel(service, initial: rule) { Name = "Winner", DiscountValueText = "15" };
        var second = new PromotionDialogViewModel(service, initial: rule) { Name = "My unsaved draft", DiscountValueText = "25" };
        var view = new PromotionDialog();
        var window = Show(view, second);
        try
        {
            await first.SaveCommand.ExecuteAsync(null);
            await second.SaveCommand.ExecuteAsync(null);
            Assert.True(second.HasConflict);
            Assert.Equal("My unsaved draft", second.Name);
            Assert.False(second.SaveCommand.CanExecute(null));
            var save = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == second.SaveCommand);
            Assert.False(save.IsEffectivelyEnabled);
            await second.SaveCommand.ExecuteAsync(null);
            Assert.Contains("cambió", second.ErrorMessage, StringComparison.Ordinal);
            var current = Assert.Single(await service.GetAllAsync());
            Assert.Equal("Winner", current.Name); Assert.Equal(15m, current.DiscountValue);
            var reopened = new PromotionDialogViewModel(service, initial: current) { Name = "Reviewed update" };
            await reopened.SaveCommand.ExecuteAsync(null);
            Assert.Empty(reopened.ErrorMessage);
            Assert.Equal("Reviewed update", Assert.Single(await service.GetAllAsync()).Name);
            await using var db = Db();
            Assert.Equal(3, await db.CommandReceipts.CountAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PromotionEditor_LostResponseKeepsExactCommandAndCannotReapplyOverANewerRule()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var wrapper = new PromotionEditorService(service) { LoseNextSaveResponse = true };
        var model = new PromotionDialogViewModel(wrapper) { Name = "Original", AllProducts = true, DiscountValueText = "10" };
        int closes = 0;
        model.RequestClose += () => closes++;
        var view = new PromotionDialog(); var window = Show(view, model);
        try
        {
            await model.SaveCommand.ExecuteAsync(null);
            Assert.True(model.IsPendingConfirmation);
            Assert.False(model.CanEditFields);
            Assert.True(model.CanSave);
            Assert.Equal(0, closes);
            Assert.Contains("mismo guardado", model.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("revisar", model.CloseButtonText, StringComparison.Ordinal);
            var stored = Assert.Single(await service.GetAllAsync());
            await service.SaveAsync(stored with { Name = "Newer operator", DiscountValue = 30m }, "EXTERNAL-EDIT");
            model.Name = "Changed programmatically"; // Disabled controls and an immutable request both protect the retry.
            await ClickTwiceAsync(view, model.SaveCommand);
            Assert.False(model.IsPendingConfirmation);
            Assert.Equal(1, closes);
            Assert.Equal(2, wrapper.Requests.Count);
            Assert.Equal(wrapper.Requests[0].Operation, wrapper.Requests[1].Operation);
            Assert.Same(wrapper.Requests[0].Rule, wrapper.Requests[1].Rule);
            var current = Assert.Single(await service.GetAllAsync());
            Assert.Equal("Newer operator", current.Name);
            Assert.Equal(30m, current.DiscountValue);
            await using var db = Db();
            Assert.Equal(2, await db.TransactionJournal.CountAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionEditor_DismissedSaveCannotCloseReplacementOrRestoreItsFeedback(bool fail)
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new PromotionEditorService(service) { BeforeSave = completion.Task };
        var model = new PromotionDialogViewModel(wrapper) { Name = "Pending", AllProducts = true, DiscountValueText = "10" };
        var coordinator = new DialogCoordinator();
        coordinator.ShowDialog(model);
        int closes = 0;
        model.RequestClose += () => { closes++; coordinator.TryCloseDialog(model); };
        var pending = model.SaveCommand.ExecuteAsync(null);
        Assert.False(pending.IsCompleted);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Single(wrapper.Requests);
        var replacement = new PromotionDialogViewModel(service);
        coordinator.ShowDialog(replacement);
        if (fail) completion.SetException(new IOException("Synthetic late failure."));
        else completion.SetResult();
        await pending;
        Assert.Equal(0, closes);
        Assert.Same(replacement, coordinator.ActiveDialogViewModel);
        Assert.Empty(model.ErrorMessage);
        Assert.False(model.CanSave);
        Assert.False(model.IsPendingConfirmation);
        await using var db = Db();
        Assert.Equal(fail ? 0 : 1, await db.Promotions.CountAsync());
    }

    [AvaloniaTheory]
    [InlineData("2030-06-01", "2030-06-02", "2030-06-01T00:00:00.0000000", "2030-06-02T23:59:59.9999999")]
    [InlineData("2030-06-01 13:14", "2030-06-01 15:16", "2030-06-01T13:14:00.0000000", "2030-06-01T15:16:00.0000000")]
    [InlineData("2030-06-01 13:14:15.1234567", "2030-06-01 15:16:17.7654321", "2030-06-01T13:14:15.1234567", "2030-06-01T15:16:17.7654321")]
    [InlineData("9999-12-31", "9999-12-31", "9999-12-31T00:00:00.0000000", "9999-12-31T23:59:59.9999999")]
    public async Task PromotionEditor_ExplicitSchedulePrecisionAndDateOnlyEndAreStable(string start, string end, string expectedStart, string expectedEnd)
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var model = new PromotionDialogViewModel(service)
        { Name = "Schedule", AllProducts = true, DiscountValueText = "10", StartDateText = start, EndDateText = end };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Empty(model.ErrorMessage);
        await using var db = Db();
        var rule = await db.Promotions.SingleAsync();
        Assert.Equal(expectedStart, rule.StartDate);
        Assert.Equal(expectedEnd, rule.EndDate);
        var editor = new PromotionDialogViewModel(service, initial: Assert.Single(await service.GetAllAsync())) { Name = "Renamed" };
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Empty(editor.ErrorMessage);
        var edited = await db.Promotions.AsNoTracking().SingleAsync();
        Assert.Equal(expectedStart, edited.StartDate);
        Assert.Equal(expectedEnd, edited.EndDate);
    }

    [AvaloniaTheory]
    [InlineData("invalid", "2030-06-01", "10")]
    [InlineData("2030-06-02", "2030-06-01", "10")]
    [InlineData("2030-06-01", "2030-06-02", "101")]
    public async Task PromotionEditor_KnownValidationFailureAllowsCorrectionWithoutCreatingReceipt(string start, string end, string discount)
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var model = new PromotionDialogViewModel(service)
        { Name = "Invalid", AllProducts = true, DiscountValueText = discount, StartDateText = start, EndDateText = end };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(model.ErrorMessage);
        Assert.False(model.IsPendingConfirmation);
        Assert.False(model.HasConflict);
        Assert.True(model.CanSave);
        Assert.True(model.CanEditFields);
        await using var db = Db();
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.Promotions.ToListAsync());
        model.StartDateText = "2030-06-01"; model.EndDateText = "2030-06-02"; model.DiscountValueText = "10";
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Empty(model.ErrorMessage);
        Assert.Single(await db.Promotions.ToListAsync());
        Assert.Single(await db.CommandReceipts.ToListAsync());
    }

    [AvaloniaFact]
    public async Task PromotionWorkspace_CommandsFromClearedOrReplacedRowsCannotModifyRulesOrOpenEditor()
    {
        await SeedPromotionWorkspaceAsync(1);
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var model = new PromotionsViewModel(service);
        await model.InitializationTask;
        var old = Assert.Single(model.Promotions);
        int editors = 0;
        model.OpenEditPromotionRequested += _ => editors++;
        await model.RefreshCommand.ExecuteAsync(null);
        await model.ToggleStatusCommand.ExecuteAsync(old);
        await model.DeletePromotionCommand.ExecuteAsync(old);
        model.EditPromotionCommand.Execute(old);
        var current = Assert.Single(model.Promotions);
        Assert.True(current.IsActive);
        model.ClearSessionData();
        await model.ToggleStatusCommand.ExecuteAsync(current);
        model.EditPromotionCommand.Execute(current);
        Assert.Equal(0, editors);
        await using var db = Db();
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    private sealed class PromotionEditorService(IPromotionService inner) : IPromotionService
    {
        public Task? BeforeSave { get; init; }
        public bool LoseNextSaveResponse { get; set; }
        public List<(PromotionDto Rule, string Operation)> Requests { get; } = [];
        public Task<List<PromotionDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<List<PromotionDto>> GetActiveAsync() => inner.GetActiveAsync();
        public Task<CatalogPage<PromotionDto>> GetPageAsync(int page, int size = 100, string? search = null) => inner.GetPageAsync(page, size, search);
        public Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> items) => inner.EvaluateAndApplyPromotionsAsync(items);
        public Task DeleteAsync(string id, string operationId) => inner.DeleteAsync(id, operationId);
        public Task SetActiveAsync(string id, bool active, string operationId) => inner.SetActiveAsync(id, active, operationId);
        public async Task SaveAsync(PromotionDto promotion, string operationId)
        {
            Requests.Add((promotion, operationId));
            if (BeforeSave is not null) await BeforeSave;
            await inner.SaveAsync(promotion, operationId);
            if (LoseNextSaveResponse)
            { LoseNextSaveResponse = false; throw new IOException("Synthetic lost save response."); }
        }
    }
}
