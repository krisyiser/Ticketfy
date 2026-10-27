using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Services;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Directories;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task KitEditor_DirectorySelectsProductNotRecipeAndSavesWithRealFormBindings()
    {
        await SeedGraphKitAsync("LINKED-PRODUCT", ("P", 2));
        var service = KitsService(); var directory = new KitDirectoryViewModel(service);
        KitDraft? draft = null; directory.OpenEditRequested += value => draft = value;
        var view = new KitDirectoryView(); var window = Show(view, directory);
        try
        {
            await directory.LoadAsync();
            var table = view.GetVisualDescendants().OfType<DataGrid>().First();
            table.SelectedItem = Assert.Single(directory.Rows);
            Assert.Equal("LINKED-PRODUCT-RECIPE", directory.SelectedItem!.Id);
            await ClickTwiceAsync(view, directory.EditCommand);
            Assert.NotNull(draft); Assert.Equal("LINKED-PRODUCT", draft.ProductId);
            var editor = new ItemKitDialogViewModel(service, ProductsService(), draft);
            await editor.InitializationTask;
            var form = new ItemKitDialog(); var formWindow = Show(form, editor);
            try
            {
                Assert.Equal(2, editor.DraftKitItems.Single().Quantity);
                Assert.DoesNotContain(editor.AvailableProducts, p => p.Id == draft.ProductId);
                form.FindControl<TextBox>("KitNameField")!.Text = "Edited through the form";
                Assert.Equal("Edited through the form", editor.KitName);
                await ClickTwiceAsync(form, editor.SaveKitCommand);
                Assert.Contains("guardado", editor.FeedbackMessage);
                Assert.False(editor.CanSave);
                var stored = (await service.GetForEditAsync(draft.ProductId))!;
                Assert.Equal("Edited through the form", stored.Name);
                Assert.Equal(2, stored.Items.Single().Quantity);
                Assert.Equal(1, stored.Revision);
                await using var db = Db();
                Assert.Equal("LINKED-PRODUCT-RECIPE", (await db.ItemKits.SingleAsync()).Id);
                Assert.Single(await db.CommandReceipts.ToListAsync());
            }
            finally { formWindow.Close(); }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KitEditor_LostResponseFreezesExactCommandAndDoesNotUndoNewerEditOnConfirmation()
    {
        var service = KitsService(); var wrapper = new KitEditorService(service) { LoseSaveResponse = true };
        var editor = new ItemKitDialogViewModel(wrapper, ProductsService());
        await editor.InitializationTask;
        editor.KitName = "Original"; editor.KitPrice = 10m;
        editor.SelectedProduct = Assert.Single(editor.AvailableProducts); editor.AddItemToDraftCommand.Execute(null);
        int closed = 0; editor.RequestClose += () => closed++;
        var form = new ItemKitDialog(); var window = Show(form, editor);
        try
        {
            await editor.SaveKitCommand.ExecuteAsync(null);
            Assert.True(editor.IsPendingConfirmation); Assert.False(editor.CanEditFields); Assert.Equal(0, closed);
            Assert.False(form.FindControl<TextBox>("KitNameField")!.IsEffectivelyEnabled);
            Assert.StartsWith("COMBO-", editor.KitBarcode);
            var saved = wrapper.Saves.Single().Draft;
            var newer = (await service.GetForEditAsync(saved.ProductId))!;
            await service.SaveAsync(newer with { Name = "Another editor" }, "NEWER");
            editor.KitName = "Changed programmatically"; editor.DraftKitItems.Clear();
            await ClickTwiceAsync(form, editor.SaveKitCommand);
            Assert.Equal(1, closed); Assert.False(editor.IsPendingConfirmation);
            Assert.Equal(2, wrapper.Saves.Count);
            Assert.Same(wrapper.Saves[0].Draft, wrapper.Saves[1].Draft);
            Assert.Equal(wrapper.Saves[0].Operation, wrapper.Saves[1].Operation);
            Assert.Equal("Another editor", (await service.GetForEditAsync(saved.ProductId))!.Name);
            await using var db = Db(); Assert.Equal(2, await db.TransactionJournal.CountAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KitEditor_StaleDraftRequiresReopenAndConfirmedSaveSurvivesCloseHandlerFailure()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var draft = (await service.GetForEditAsync("EDIT-KIT"))!;
        var stale = new ItemKitDialogViewModel(service, ProductsService(), draft);
        await stale.InitializationTask;
        await service.SaveAsync(draft with { Name = "Winner" }, "OTHER");
        stale.KitName = "My draft"; await stale.SaveKitCommand.ExecuteAsync(null);
        Assert.True(stale.HasConflict); Assert.False(stale.CanSave); Assert.False(stale.CanEditFields);
        Assert.Equal("My draft", stale.KitName);
        var reopened = new ItemKitDialogViewModel(service, ProductsService(), (await service.GetForEditAsync("EDIT-KIT"))!);
        await reopened.InitializationTask;
        reopened.KitName = "Reviewed"; reopened.RequestClose += () => throw new InvalidOperationException("Synthetic close failure");
        await reopened.SaveKitCommand.ExecuteAsync(null);
        Assert.Contains("guardado", reopened.FeedbackMessage); Assert.False(reopened.IsPendingConfirmation); Assert.False(reopened.CanSave);
        await reopened.SaveKitCommand.ExecuteAsync(null);
        Assert.Equal("Reviewed", (await service.GetForEditAsync("EDIT-KIT"))!.Name);
        await using var db = Db(); Assert.Equal(3, await db.CommandReceipts.CountAsync());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KitEditor_DismissedPendingSaveCannotCloseReplacementOrRestoreFeedback(bool failure)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = KitsService(); var wrapper = new KitEditorService(service) { BeforeSave = completion.Task };
        var editor = new ItemKitDialogViewModel(wrapper, ProductsService()) { KitName = "Pending", KitPrice = 10m };
        await editor.InitializationTask; editor.SelectedProduct = Assert.Single(editor.AvailableProducts);
        editor.AddItemToDraftCommand.Execute(null);
        var coordinator = new DialogCoordinator(); coordinator.ShowDialog(editor);
        int closed = 0; editor.RequestClose += () => { closed++; coordinator.TryCloseDialog(editor); };
        var pending = editor.SaveKitCommand.ExecuteAsync(null);
        await editor.SaveKitCommand.ExecuteAsync(null);
        Assert.Single(wrapper.Saves);
        var replacement = new ItemKitDialogViewModel(service, ProductsService());
        coordinator.ShowDialog(replacement); await replacement.InitializationTask;
        if (failure) completion.SetException(new IOException("Synthetic delayed failure")); else completion.SetResult();
        await pending;
        Assert.Same(replacement, coordinator.ActiveDialogViewModel); Assert.Equal(0, closed);
        Assert.Empty(editor.FeedbackMessage); Assert.Empty(editor.DraftKitItems); Assert.Empty(editor.AvailableProducts);
        Assert.False(editor.CanSave); Assert.False(editor.IsPendingConfirmation);
        await using var db = Db(); Assert.Equal(failure ? 0 : 1, await db.ItemKits.CountAsync());
    }

    [AvaloniaFact]
    public async Task KitEditor_KnownRejectionAllowsCorrectionWithoutPersistingPartialRecipe()
    {
        var service = KitsService(); var editor = new ItemKitDialogViewModel(service, ProductsService());
        await editor.InitializationTask;
        await editor.SaveKitCommand.ExecuteAsync(null);
        Assert.Contains("nombre", editor.FeedbackMessage);
        editor.KitName = "Correctable"; editor.KitPrice = 1.001m;
        await editor.SaveKitCommand.ExecuteAsync(null); Assert.True(editor.CanEditFields);
        editor.KitPrice = 10m; editor.SelectedProduct = Assert.Single(editor.AvailableProducts);
        editor.ItemQuantity = .5; editor.AddItemToDraftCommand.Execute(null); Assert.Empty(editor.DraftKitItems);
        editor.ItemQuantity = 1; editor.AddItemToDraftCommand.Execute(null);
        editor.KitBarcode = "BARCODE"; await editor.SaveKitCommand.ExecuteAsync(null);
        Assert.Contains("otro producto", editor.FeedbackMessage);
        Assert.True(editor.CanEditFields); Assert.False(editor.IsPendingConfirmation);
        await using var db = Db(); Assert.Empty(await db.CommandReceipts.ToListAsync());
        editor.KitBarcode = "VALID"; await editor.SaveKitCommand.ExecuteAsync(null);
        Assert.Contains("guardado", editor.FeedbackMessage); Assert.Single(await db.ItemKits.ToListAsync());
    }

    [AvaloniaFact]
    public async Task KitEditor_StatusLostResponseKeepsSameCommandAcrossSelectionAndRefresh()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var wrapper = new KitEditorService(service) { LoseStatusResponse = true };
        var model = new KitDirectoryViewModel(wrapper);
        var view = new KitDirectoryView(); var window = Show(view, model);
        try
        {
            await model.LoadAsync(); model.SelectedItem = Assert.Single(model.Rows);
            await model.ToggleStatusCommand.ExecuteAsync(null);
            Assert.Contains("mismo cambio", model.ActionMessage);
            Assert.False(model.CanEdit); Assert.False(model.CanCreate); Assert.True(model.CanToggle);
            Assert.False((await service.GetForEditAsync("EDIT-KIT"))!.IsActive);
            await service.SetActiveAsync("EDIT-KIT", true, "NEWER");
            await model.RefreshCommand.ExecuteAsync(null); model.SelectedItem = null;
            await ClickTwiceAsync(view, model.ToggleStatusCommand);
            Assert.Equal(2, wrapper.Statuses.Count);
            Assert.Equal(wrapper.Statuses[0], wrapper.Statuses[1]);
            Assert.Contains("confirmada", model.ActionMessage);
            Assert.True((await service.GetForEditAsync("EDIT-KIT"))!.IsActive);
            await using var db = Db(); Assert.Equal(3, await db.CommandReceipts.CountAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KitEditor_ConfirmedStatusDoesNotBecomeUncertainWhenDirectoryRefreshFails()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var wrapper = new KitEditorService(service);
        var model = new KitDirectoryViewModel(wrapper);
        await model.LoadAsync(); model.SelectedItem = Assert.Single(model.Rows);
        wrapper.FailPages = true;
        await model.ToggleStatusCommand.ExecuteAsync(null);
        Assert.Contains("sí quedó confirmado", model.ActionMessage);
        Assert.False(model.CanToggle); Assert.Empty(model.Rows);
        Assert.False((await service.GetForEditAsync("EDIT-KIT"))!.IsActive);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KitEditor_SelectionOrSessionChangeDiscardsDelayedEditor(bool clearSession)
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new KitEditorService(service) { BeforeEdit = pending.Task };
        var model = new KitDirectoryViewModel(wrapper);
        await model.LoadAsync(); model.SelectedItem = Assert.Single(model.Rows);
        int opened = 0; model.OpenEditRequested += _ => opened++;
        var reading = model.EditCommand.ExecuteAsync(null);
        if (clearSession) model.ClearSessionData(); else model.SelectedItem = null;
        pending.SetResult(); await reading;
        Assert.Equal(0, opened); Assert.Empty(model.ActionMessage); Assert.False(model.CanEdit);
    }

    [AvaloniaFact]
    public async Task KitEditor_UiPermissionsAndSessionClearPreventCommandsFromRetainedRows()
    {
        var service = KitsService(); await service.SaveAsync(NewKit(), "CREATE");
        var wrapper = new KitEditorService(service) { Capabilities = new(false, false, false) };
        var model = new KitDirectoryViewModel(wrapper); var view = new KitDirectoryView(); var window = Show(view, model);
        try
        {
            await model.LoadAsync(); var row = Assert.Single(model.Rows); model.SelectedItem = row;
            Assert.False(view.FindControl<Button>("EditKitButton")!.IsEffectivelyEnabled);
            Assert.False(view.FindControl<Button>("KitStatusButton")!.IsEffectivelyEnabled);
            Assert.False(model.CanCreate);
            await model.EditCommand.ExecuteAsync(null); await model.ToggleStatusCommand.ExecuteAsync(null);
            model.ClearSessionData(); model.SelectedItem = row;
            await model.ToggleStatusCommand.ExecuteAsync(null); Assert.Empty(wrapper.Statuses);
            Assert.Empty(model.ActionMessage); Assert.False(model.CanEdit);
        }
        finally { window.Close(); }
    }

    private sealed class KitEditorService(IItemKitService inner) : IItemKitService
    {
        public bool LoseSaveResponse { get; set; }
        public bool LoseStatusResponse { get; set; }
        public bool FailPages { get; set; }
        public Task? BeforeSave { get; init; }
        public Task? BeforeEdit { get; init; }
        public KitCapabilities? Capabilities { get; init; }
        public List<(KitDraft Draft, string Operation)> Saves { get; } = [];
        public List<(string Id, bool Active, string Operation)> Statuses { get; } = [];
        public Task<KitCapabilities> GetCapabilitiesAsync() => Capabilities is null ? inner.GetCapabilitiesAsync() : Task.FromResult(Capabilities);
        public async Task<KitDraft?> GetForEditAsync(string productId)
        { if (BeforeEdit is not null) await BeforeEdit; return await inner.GetForEditAsync(productId); }
        public Task<List<ItemKitDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<ItemKitDto?> GetByBarcodeAsync(string barcode) => inner.GetByBarcodeAsync(barcode);
        public Task<CatalogPage<KitDirectoryRow>> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false) =>
            FailPages ? throw new IOException("Synthetic directory failure") : inner.GetPageAsync(page, size, search, includeInactive);
        public Task<KitComponentPage?> GetComponentsPageAsync(string id, int page, int size = 100) => inner.GetComponentsPageAsync(id, page, size);
        public Task<bool> DeductKitStockAsync(string kitId, double kitQuantity) => inner.DeductKitStockAsync(kitId, kitQuantity);
        public async Task SaveAsync(KitDraft draft, string operationId)
        {
            Saves.Add((draft, operationId)); if (BeforeSave is not null) await BeforeSave;
            await inner.SaveAsync(draft, operationId);
            if (LoseSaveResponse) { LoseSaveResponse = false; throw new InvalidOperationException("Synthetic lost response after commit"); }
        }
        public async Task SetActiveAsync(string productId, bool active, string operationId)
        {
            Statuses.Add((productId, active, operationId)); await inner.SetActiveAsync(productId, active, operationId);
            if (LoseStatusResponse) { LoseStatusResponse = false; throw new IOException("Synthetic lost status response"); }
        }
    }

    [AvaloniaFact]
    public async Task KitEditor_CanRepairInvalidLegacyQuantityWithoutCrashingOrChangingItImplicitly()
    {
        var service = KitsService();
        await SeedGraphKitAsync("REPAIR", ("P", .5));
        var editor = new ItemKitDialogViewModel(service, ProductsService(), (await service.GetForEditAsync("REPAIR"))!);
        await editor.InitializationTask;
        editor.SelectedProduct = editor.AvailableProducts.Single(p => p.Id == "P");
        editor.AddItemToDraftCommand.Execute(null);
        Assert.Contains("Quítelo", editor.FeedbackMessage);
        Assert.Equal(.5, editor.DraftKitItems.Single().Quantity);
        editor.RemoveItemFromDraftCommand.Execute(editor.DraftKitItems.Single());
        editor.AddItemToDraftCommand.Execute(null); await editor.SaveKitCommand.ExecuteAsync(null);
        Assert.Contains("guardado", editor.FeedbackMessage);
        Assert.Equal(1, (await service.GetForEditAsync("REPAIR"))!.Items.Single().Quantity);
    }
}
