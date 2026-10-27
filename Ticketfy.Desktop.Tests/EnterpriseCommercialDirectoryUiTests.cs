using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Directories;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task WalletDirectoryUi_PagesFullTotalsAndSelectsOnlyAVisibleRecord()
    {
        await SeedCommercialDirectoriesAsync(251);
        string chosen = "";
        var model = new WalletDirectoryViewModel(new GiftcardService(new TestDbContextFactory(_options), _actor), x => chosen = x);
        var view = new WalletDirectoryView(); var window = Show(view, model);
        try
        {
            await model.LoadAsync();
            Assert.Equal(100, model.Rows.Count);
            Assert.Contains("252", model.Summary);
            Assert.Contains(102.51m.ToString("C2"), model.Summary);
            var table = Assert.Single(view.GetVisualDescendants().OfType<DataGrid>());
            table.SelectedItem = model.Rows[0];
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, model.NextCommand));
            Assert.True(next.IsEffectivelyEnabled);
            next.Command!.Execute(next.CommandParameter);
            await model.NextCommand.ExecutionTask!;
            Assert.Equal(100, model.Rows.Count); Assert.Null(model.SelectedItem);
            await model.NextCommand.ExecuteAsync(null); Assert.Equal(52, model.Rows.Count); Assert.False(model.CanNext);
            await model.PreviousCommand.ExecuteAsync(null); Assert.Contains("Página 2", model.PageLabel);
            model.SearchQuery = "CARD-0250"; Assert.Empty(model.Rows); Assert.Empty(model.Summary);
            await model.SearchCommand.ExecuteAsync(null);
            var row = Assert.Single(model.Rows); table.SelectedItem = row;
            Assert.Same(row, model.SelectedItem);
            model.UseSelectedCommand.Execute(null); Assert.Equal("CARD-0250", chosen);
            chosen = "";
            model.IncludeInactive = true; model.SelectedItem = row; model.UseSelectedCommand.Execute(null);
            Assert.Empty(chosen); Assert.Empty(model.Rows);
            await model.RefreshCommand.ExecuteAsync(null); Assert.Single(model.Rows);
            model.ClearSessionData();
            Assert.Empty(model.Rows); Assert.Empty(model.Summary); Assert.Empty(model.SearchQuery); Assert.False(model.IncludeInactive);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KitDirectoryUi_IsReachableFromPromotionsAndBrowsesEveryComponent()
    {
        await SeedCommercialDirectoriesAsync(1);
        await using (var db = Db())
        {
            db.ItemKitItems.AddRange(Enumerable.Range(1, 200).Select(i => new ItemKitItemEntity
            { Id = $"EXTRA-{i:0000}", ItemKitId = "KIT-0000", ProductId = "P", Quantity = 1 }));
            await db.SaveChangesAsync();
        }
        var factory = new TestDbContextFactory(_options);
        var owner = new PromotionsViewModel(new PromotionService(factory, _actor), new ItemKitService(factory, _actor));
        await owner.InitializationTask;
        var view = new PromotionsView(); var window = Show(view, owner);
        try
        {
            var toggle = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), c => c.Content?.ToString() == "Consultar directorio y composición de combos");
            toggle.IsChecked = true; await owner.KitLoadTask;
            var directory = Assert.Single(view.GetVisualDescendants().OfType<KitDirectoryView>());
            Assert.True(directory.IsEffectivelyVisible);
            var model = owner.Kits!;
            model.SelectedItem = Assert.Single(model.Rows);
            await model.ReadComponentsCommand.ExecuteAsync(null);
            Assert.Equal(100, model.Components.Count); Assert.Contains("201", model.CompositionSummary);
            await model.OlderComponentsCommand.ExecuteAsync(null); Assert.Equal(100, model.Components.Count);
            await model.OlderComponentsCommand.ExecuteAsync(null); Assert.Single(model.Components); Assert.False(model.HasMoreComponents);
            await model.PreviousComponentsCommand.ExecuteAsync(null); Assert.Equal(100, model.Components.Count);
            model.SelectedItem = null; Assert.Empty(model.Components); Assert.Empty(model.CompositionSummary);
            model.SelectedItem = model.Rows[0]; await model.ReadComponentsCommand.ExecuteAsync(null);
            owner.ClearSessionData();
            Assert.False(owner.ShowKits); Assert.Empty(model.Rows); Assert.Empty(model.Components);
            Assert.Empty(model.CompositionSummary);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WalletDirectoryUi_IsConnectedToCommercialControlWithoutPerformingMoneyMovements()
    {
        await SeedCommercialDirectoriesAsync(1);
        var factory = new TestDbContextFactory(_options);
        var model = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor),
            new PurchaseService(factory, _actor), new ShiftService(factory, _actor), new GiftcardService(factory, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor, Sales);
        await model.RefreshAsync();
        var view = new CommercialControlView(); var window = Show(view, model);
        try
        {
            var tabs = Assert.Single(view.GetVisualDescendants().OfType<TabControl>());
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Monedero");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            window.UpdateLayout();
            var expander = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), e => e.Header?.ToString() == "Buscar y consultar monederos");
            expander.IsExpanded = true;
            window.UpdateLayout();
            var directoryView = Assert.Single(view.GetVisualDescendants().OfType<WalletDirectoryView>());
            Assert.Same(model.WalletDirectory, directoryView.DataContext);
            var table = Assert.Single(directoryView.GetVisualDescendants().OfType<DataGrid>());
            table.SelectedItem = model.WalletDirectory.Rows.Single(w => w.CardNumber == "WALLET");
            model.WalletDirectory.UseSelectedCommand.Execute(null);
            Assert.Equal("WALLET", model.WalletNumber);
            await using var db = Db();
            Assert.Empty(db.ShiftMovements); Assert.Empty(db.TransactionJournal);
            model.ClearDirectoryData();
            Assert.Empty(model.WalletDirectory.Rows);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryUi_LateSuccessAndFailureCannotRestorePreviousSession(bool fail)
    {
        var pending = new TaskCompletionSource<DirectoryRead<WalletDirectoryRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new DeferredCommercialDirectory { Read = () => pending.Task };
        var load = model.LoadAsync();
        model.ClearSessionData();
        if (fail) pending.SetException(new IOException("Synthetic private detail"));
        else pending.SetResult(new([new("id", "number", 99m, "customer", true)], false, "Private balance"));
        await load;
        Assert.Empty(model.Rows); Assert.Empty(model.Summary); Assert.Empty(model.ErrorMessage);
        Assert.False(model.IsLoading); Assert.False(model.HasLoadedData);
    }

    [AvaloniaFact]
    public async Task DirectoryUi_ChangedFilterInvalidatesPendingReadAndFailureRemovesBalances()
    {
        var pending = new TaskCompletionSource<DirectoryRead<WalletDirectoryRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new DeferredCommercialDirectory { Read = () => pending.Task };
        var oldRead = model.LoadAsync();
        model.SearchQuery = "new filter";
        model.Read = () => Task.FromResult(new DirectoryRead<WalletDirectoryRow>([new("new", "new", .01m, null, true)], false, "New balance"));
        await model.SearchCommand.ExecuteAsync(null);
        pending.SetResult(new([new("old", "old", 99m, null, true)], false, "Old balance"));
        await oldRead;
        Assert.Equal("new", Assert.Single(model.Rows).Id); Assert.Equal("New balance", model.Summary);
        model.Read = () => Task.FromException<DirectoryRead<WalletDirectoryRow>>(new IOException("Synthetic private detail"));
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(model.Rows); Assert.Empty(model.Summary); Assert.False(model.HasLoadedData);
        Assert.NotEmpty(model.ErrorMessage); Assert.DoesNotContain("private", model.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task KitDirectoryUi_DiscardsLateComponentsAfterSelectionOrSessionChange()
    {
        var service = new DeferredKitDirectory();
        var model = new KitDirectoryViewModel(service);
        await model.LoadAsync();
        var row = model.Rows[0]; model.SelectedItem = row;
        var pending = model.ReadComponentsCommand.ExecuteAsync(null);
        model.SelectedItem = model.Rows[1];
        service.Pending.SetResult(new(row, "old recipe", [new("part", row.Id, "P", "Secret name", 1)], false));
        await pending;
        Assert.Empty(model.Components); Assert.Empty(model.CompositionSummary);
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = model.ReadComponentsCommand.ExecuteAsync(null);
        model.ClearSessionData();
        service.Pending.SetException(new IOException("Private recipe"));
        await pending;
        Assert.Empty(model.Components); Assert.Empty(model.CompositionSummary);
        Assert.False(model.IsReadingComponents);
    }

    private sealed class DeferredCommercialDirectory : PagedDirectoryViewModel<WalletDirectoryRow>
    {
        public required Func<Task<DirectoryRead<WalletDirectoryRow>>> Read { get; set; }
        protected override Task<DirectoryRead<WalletDirectoryRow>> ReadAsync(int page, string search, bool inactive) => Read();
    }
    private sealed class DeferredKitDirectory : IItemKitService
    {
        public TaskCompletionSource<KitComponentPage?> Pending { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CatalogPage<KitDirectoryRow>> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false) =>
            Task.FromResult(new CatalogPage<KitDirectoryRow>([new("A", "A", "A", "A", 1m, true, 1, false), new("B", "B", "B", "B", 2m, true, 1, false)], false));
        public Task<KitComponentPage?> GetComponentsPageAsync(string id, int page, int size = 100) => Pending.Task;
        public Task<List<ItemKitDto>> GetAllAsync() => throw new NotSupportedException();
        public Task<ItemKitDto?> GetByBarcodeAsync(string barcode) => throw new NotSupportedException();
        public Task<KitCapabilities> GetCapabilitiesAsync() => Task.FromResult(new KitCapabilities(true, true, true));
        public Task<KitDraft?> GetForEditAsync(string productId) => throw new NotSupportedException();
        public Task SaveAsync(KitDraft draft, string operationId) => throw new NotSupportedException();
        public Task SetActiveAsync(string productId, bool active, string operationId) => throw new NotSupportedException();
        public Task<bool> DeductKitStockAsync(string kitId, double kitQuantity) => throw new NotSupportedException();
    }
}
