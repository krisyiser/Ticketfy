using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Inventory;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task PredictiveUi_LatestCartRequestWinsAndOlderResponseCannotReplaceIt()
    {
        var service = new DeferredPredictiveService();
        var clock = new SuggestionTestClock();
        using var pos = new PosViewModel(ProductsService(), predictiveService: service, externalCatalogService: new NoNetworkCatalog(), suggestionClock: clock);
        var product = (await ProductsService().GetByIdAsync("P"))!;
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE-1", "First", 10m, 1), 10);
        var first = await service.NextSuggestion();
        Task firstWork = pos.SuggestionCompletion;
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE-2", "Second", 10m, 1), 10);
        var second = await service.NextSuggestion();
        Task secondWork = pos.SuggestionCompletion;
        Assert.True(first.Token.IsCancellationRequested);
        var visible = WhenSuggestionVisible(pos);
        second.Response.SetResult(product with { Name = "Current suggestion" });
        await visible;
        first.Response.SetResult(product with { Name = "Obsolete suggestion" });
        await firstWork;
        Assert.Contains("Current suggestion", pos.SmartSuggestionText);
        Assert.True(pos.IsSuggestionVisible);
        clock.Fire(0);
        await secondWork;
        Assert.False(pos.IsSuggestionVisible); Assert.Empty(pos.SmartSuggestionText);
        pos.AddSuggestionToCartCommand.Execute(null);
        Assert.Equal(2, pos.Cart.CartState.Items.Count);
    }

    [AvaloniaFact]
    public async Task PredictiveUi_RepeatedProductSuggestionHasItsOwnExpiryAndDoubleClickAddsOnce()
    {
        var service = new DeferredPredictiveService();
        var clock = new SuggestionTestClock();
        using var pos = new PosViewModel(ProductsService(), predictiveService: service, externalCatalogService: new NoNetworkCatalog(), suggestionClock: clock);
        var product = (await ProductsService().GetByIdAsync("P"))!;
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE-1", "First", 10m, 1), 10);
        var first = await service.NextSuggestion();
        var shown = WhenSuggestionVisible(pos); first.Response.SetResult(product); await shown;
        Task firstWork = pos.SuggestionCompletion;
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE-2", "Second", 10m, 1), 10);
        var second = await service.NextSuggestion();
        shown = WhenSuggestionVisible(pos); second.Response.SetResult(product); await shown;
        await firstWork;
        clock.Fire(0); // A callback already queued by the disposed earlier timer must be harmless.
        Assert.True(pos.IsSuggestionVisible);
        Task secondWork = pos.SuggestionCompletion;
        pos.AddSuggestionToCartCommand.Execute(null); pos.AddSuggestionToCartCommand.Execute(null);
        Assert.Equal(1d, pos.Cart.CartState.Items.Single(p => p.ProductId == "P").Quantity);
        await secondWork;
        var afterAdd = await service.NextSuggestion();
        Assert.Contains("P", afterAdd.Cart);
        afterAdd.Response.SetResult(product); // Caller also refuses a product that is already in the cart.
        await pos.SuggestionCompletion;
        Assert.False(pos.IsSuggestionVisible);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PredictiveUi_ClearOrDisposeInvalidatesPendingAndObservedFailures(bool dispose)
    {
        var service = new DeferredPredictiveService();
        using var pos = new PosViewModel(ProductsService(), predictiveService: service, externalCatalogService: new NoNetworkCatalog());
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE", "First", 10m, 1), 10);
        var call = await service.NextSuggestion();
        Task work = pos.SuggestionCompletion;
        if (dispose) pos.Dispose(); else pos.Cart.CartState.Clear();
        Assert.True(call.Token.IsCancellationRequested);
        call.Response.SetException(new IOException("Synthetic late failure"));
        await work;
        Assert.False(pos.IsSuggestionVisible); Assert.Empty(pos.SmartSuggestionText);
        pos.AddSuggestionToCartCommand.Execute(null);
        Assert.DoesNotContain(pos.Cart.CartState.Items, p => p.ProductId == "P");
        if (dispose)
        {
            pos.Cart.CartState.AddItem(new CartItemDto("OTHER", "Other", 10m, 1), 10);
            Assert.False(service.Suggestions.Reader.TryRead(out _));
        }
    }

    [AvaloniaFact]
    public async Task PredictiveUi_InventoryHidesOldDataWhileLoadingAndDisplaysFailureNotZero()
    {
        var service = new DeferredPredictiveService();
        var model = new InventoryViewModel(ProductsService(), new NoNetworkCatalog(), predictiveService: service);
        var view = new InventoryView { DataContext = model };
        var window = new Window { Content = view }; window.Show();
        try
        {
            var first = await service.NextRestock();
            first.Response.SetResult([new() { Message = "Old estimate", DaysRemaining = 1 }]);
            await model.InitialLoad;
            Assert.Single(model.UrgentRestockAlerts);
            var retry = model.IntelligenceVM.RefreshAlertsAsync();
            var next = await service.NextRestock();
            Assert.Empty(model.UrgentRestockAlerts);
            Assert.Contains("Consultando", model.IntelligenceVM.Status);
            next.Response.SetException(new IOException("Synthetic read failure"));
            await retry;
            Assert.True(model.IsIntelligencePanelVisible);
            Assert.Contains("no disponible", model.IntelligenceVM.Status);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.IntelligenceVM.Status && t.IsEffectivelyVisible);
            Assert.Empty(model.UrgentRestockAlerts);
            model.ClearSessionData();
            Assert.False(model.IsIntelligencePanelVisible); Assert.Empty(model.IntelligenceVM.Status);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PredictiveUi_InventoryReplacementAndLogoutCancelAndDiscardLateResponses()
    {
        var service = new DeferredPredictiveService();
        var model = new InventoryIntelligenceViewModel(service);
        Task firstWork = model.RefreshAlertsAsync(); var first = await service.NextRestock();
        Task secondWork = model.RefreshAlertsAsync(); var second = await service.NextRestock();
        Assert.True(first.Token.IsCancellationRequested);
        second.Response.SetResult(Enumerable.Range(0, 20).Select(i => new PredictiveAlertDto { Message = $"Current-{i}" }).ToList());
        await secondWork;
        first.Response.SetResult([new() { Message = "Stale" }]); await firstWork;
        Assert.Equal(3, model.UrgentRestockAlerts.Count);
        Assert.Contains("3 estimaciones", model.Status);
        Task thirdWork = model.RefreshAlertsAsync(); var third = await service.NextRestock();
        model.ClearSessionData();
        Assert.True(third.Token.IsCancellationRequested);
        third.Response.SetResult([new() { Message = "From previous session" }]); await thirdWork;
        Assert.Empty(model.UrgentRestockAlerts); Assert.Empty(model.Status); Assert.False(model.IsIntelligencePanelVisible);
    }

    private static Task WhenSuggestionVisible(PosViewModel model)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsSuggestionVisible) && model.IsSuggestionVisible)
            { model.PropertyChanged -= handler; done.SetResult(); }
        };
        model.PropertyChanged += handler;
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [AvaloniaFact]
    public async Task PredictiveUi_LogoutDiscardsLateSuccessAndFreshFailureLeavesNoClickableSuggestion()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var service = new DeferredPredictiveService();
        var product = (await ProductsService().GetByIdAsync("P"))!;
        using var pos = new PosViewModel(ProductsService(), sessionManager: session, predictiveService: service, externalCatalogService: new NoNetworkCatalog());
        pos.Cart.CartState.AddItem(new CartItemDto("SOURCE", "First", 10m, 1), 10);
        var late = await service.NextSuggestion(); Task beforeLogout = pos.SuggestionCompletion;
        session.ClearSession();
        Assert.True(late.Token.IsCancellationRequested);
        late.Response.SetResult(product);
        await beforeLogout;
        Assert.Empty(pos.Cart.CartState.Items); Assert.False(pos.IsSuggestionVisible);
        // A failing provider never leaves a previous result actionable.
        pos.Cart.CartState.AddItem(new CartItemDto("SYNTHETIC-NEW", "New", 10m, 1), 10);
        var failing = await service.NextSuggestion();
        failing.Response.SetException(new IOException("Synthetic lookup failure"));
        await pos.SuggestionCompletion;
        Assert.Empty(pos.SmartSuggestionText); Assert.False(pos.IsSuggestionVisible);
        pos.AddSuggestionToCartCommand.Execute(null);
        Assert.DoesNotContain(pos.Cart.CartState.Items, i => i.ProductId == "P");
    }

    private sealed record SuggestionCall(List<string> Cart, CancellationToken Token, TaskCompletionSource<ProductDto?> Response);
    private sealed record RestockCall(CancellationToken Token, TaskCompletionSource<List<PredictiveAlertDto>> Response);
    private sealed class DeferredPredictiveService : IPredictiveIntelligenceService
    {
        public Channel<SuggestionCall> Suggestions { get; } = Channel.CreateUnbounded<SuggestionCall>();
        private Channel<RestockCall> Restock { get; } = Channel.CreateUnbounded<RestockCall>();
        public Task<ProductDto?> GetTopCorrelatedProductAsync(string sourceProductId, List<string> ids, CancellationToken cancellationToken = default)
        {
            var response = new TaskCompletionSource<ProductDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Suggestions.Writer.TryWrite(new(ids, cancellationToken, response)); return response.Task;
        }
        public Task<List<PredictiveAlertDto>> GetUrgentRestockAlertsAsync(CancellationToken cancellationToken = default)
        {
            var response = new TaskCompletionSource<List<PredictiveAlertDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Restock.Writer.TryWrite(new(cancellationToken, response)); return response.Task;
        }
        public async Task<SuggestionCall> NextSuggestion() => await Suggestions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<RestockCall> NextRestock() => await Restock.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class SuggestionTestClock : TimeProvider
    {
        private readonly List<OneShotTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), dueTime); Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new OneShotTimer(callback, state); _timers.Add(timer); return timer;
        }
        public void Fire(int index) => _timers[index].Fire();
        private sealed class OneShotTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
