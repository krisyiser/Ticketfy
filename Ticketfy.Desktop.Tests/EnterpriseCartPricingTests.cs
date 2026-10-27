using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Core.State;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.Views.Pos;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseCartPricingTests
{
    [AvaloniaTheory]
    [InlineData("logout")]
    [InlineData("clear")]
    [InlineData("quantity")]
    [InlineData("customer")]
    public async Task CheckoutPreparation_DoesNotOpenForAnInvalidatedTicket(string change)
    {
        var state = new CartStateStore(); state.AddItem(new("P", "Synthetic", 10m, 1), 10);
        var pending = new TaskCompletionSource<List<CartItemDto>>();
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>();
        EnterpriseModuleActivationTests.Probe.Of(promotions).Handler = (_, _) => pending.Task;
        state.PromotionService = promotions;
        var vm = new CartViewModel(state, null!, null!, loadOnCreate: false);
        int opened = 0; vm.OpenCheckoutRequested += () => opened++;
        var preparing = vm.CheckoutCommand.ExecuteAsync("Tarjeta");
        Assert.False(preparing.IsCompleted);
        switch (change)
        {
            case "logout": vm.ClearSessionData(); break;
            case "clear": vm.ClearCartCommand.Execute(null); break;
            case "quantity": vm.IncreaseQuantityCommand.Execute(state.Items[0]); break;
            case "customer": vm.SelectedCustomer = new("C", "Otro cliente", "", "", "", 0m, 0m, 0d, 0m, ""); break;
        }
        pending.SetResult([new("P", "Synthetic", 10m, 1)]);
        await preparing;
        Assert.Equal(0, opened);
        Assert.Equal("Efectivo", vm.InitialPaymentMode);
    }

    [AvaloniaFact]
    public async Task CheckoutPreparation_DoesNotOpenAnEmptyCart()
    {
        var vm = new CartViewModel(new(), null!, null!, loadOnCreate: false);
        int opened = 0; vm.OpenCheckoutRequested += () => opened++;
        await vm.CheckoutCommand.ExecuteAsync("Efectivo");
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task OlderPricingResponse_CannotReplaceLatestDiscountForSameCartRevision()
    {
        var state = new CartStateStore(); state.AddItem(new("P", "Synthetic", 10m, 1), 10);
        var older = new TaskCompletionSource<List<CartItemDto>>(); var newer = new TaskCompletionSource<List<CartItemDto>>();
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>(); int calls = 0;
        EnterpriseModuleActivationTests.Probe.Of(promotions).Handler = (_, _) => ++calls == 1 ? older.Task : newer.Task;
        state.PromotionService = promotions;
        var oldRead = state.RefreshPromotionsAsync(); var newRead = state.RefreshPromotionsAsync();
        newer.SetResult([new("P", "Synthetic", 10m, 1) { AppliedDiscountAmount = 2m, AppliedPromotionId = "NEW" }]);
        await newRead;
        older.SetResult([new("P", "Synthetic", 10m, 1) { AppliedDiscountAmount = 1m, AppliedPromotionId = "OLD" }]);
        await oldRead;
        Assert.Equal("NEW", state.Items[0].AppliedPromotionId); Assert.Equal(8m, state.Total);
    }

    [AvaloniaTheory]
    [InlineData("lock")]
    [InlineData("lock-unlock")]
    [InlineData("logout")]
    [InlineData("switch")]
    public async Task ProductionPos_InvalidatesPendingCheckoutWhenSessionChanges(string change)
    {
        var session = new SessionManager(); session.StartSession(new UserModel { FullName = "Synthetic cashier" });
        using var pos = new PosViewModel(EnterpriseModuleActivationTests.Probe.Create<IProductService>(), sessionManager: session, loadOnCreate: false);
        pos.Cart.CartState.AddItem(new("P", "Synthetic", 10m, 1), 10);
        var pending = new TaskCompletionSource<List<CartItemDto>>();
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>();
        EnterpriseModuleActivationTests.Probe.Of(promotions).Handler = (_, _) => pending.Task;
        pos.Cart.CartState.PromotionService = promotions;
        int opened = 0; pos.OpenCheckoutRequested += () => opened++;
        var preparing = pos.Cart.CheckoutCommand.ExecuteAsync("Tarjeta");
        Assert.Contains("Comprobando", pos.Cart.CheckoutMessage);
        switch (change)
        {
            case "lock": session.LockTerminal(); break;
            case "lock-unlock": session.LockTerminal(); session.UnlockTerminal(); break;
            case "logout": session.ClearSession(); break;
            case "switch": session.SwitchCashier(new UserModel { FullName = "Another synthetic cashier" }); break;
        }
        Assert.Empty(pos.Cart.CheckoutMessage);
        pending.SetResult([new("P", "Synthetic", 10m, 1)]); await preparing;
        Assert.Equal(0, opened); Assert.Empty(pos.Cart.CheckoutMessage);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutWithoutAnUnlockedCashier_DoesNotReadOrOpen(bool locked)
    {
        var session = new SessionManager();
        if (locked) { session.StartSession(new UserModel()); session.LockTerminal(); }
        var state = new CartStateStore(); state.AddItem(new("P", "Synthetic", 10m, 1), 10);
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>(); state.PromotionService = promotions;
        var vm = new CartViewModel(state, null!, null!, loadOnCreate: false, session: session);
        int opened = 0; vm.OpenCheckoutRequested += () => opened++;
        await vm.CheckoutCommand.ExecuteAsync("Efectivo");
        Assert.Equal(0, opened); Assert.Empty(EnterpriseModuleActivationTests.Probe.Of(promotions).Calls);
    }

    [Fact]
    public async Task OlderFailure_CannotEndLatestCalculationOrShowAnObsoleteError()
    {
        var state = new CartStateStore(); state.AddItem(new("P", "Synthetic", 10m, 1), 10);
        var older = new TaskCompletionSource<List<CartItemDto>>(); var newer = new TaskCompletionSource<List<CartItemDto>>();
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>(); int calls = 0;
        EnterpriseModuleActivationTests.Probe.Of(promotions).Handler = (_, _) => ++calls == 1 ? older.Task : newer.Task;
        state.PromotionService = promotions;
        var oldRead = state.RefreshPromotionsAsync(); var newRead = state.RefreshPromotionsAsync();
        older.SetException(new IOException("synthetic obsolete failure"));
        Assert.False(await oldRead); Assert.True(state.IsPricing); Assert.Empty(state.PricingError);
        newer.SetResult([new("P", "Synthetic", 10m, 1) { AppliedDiscountAmount = 2m, AppliedPromotionId = "NEW" }]);
        Assert.True(await newRead); Assert.False(state.IsPricing); Assert.Empty(state.PricingFeedback);
        Assert.Equal(2m, state.DiscountTotal); Assert.Equal(10m, state.Subtotal); Assert.Equal(8m, state.Total);
    }

    [AvaloniaFact]
    public async Task TicketView_ShowsFailureThenActualDiscountAndOpensOnlyOnceOnRepeatedClicks()
    {
        var state = new CartStateStore(); state.AddItem(new("P", "Synthetic", 10m, .333, "kg"), 10);
        var promotions = EnterpriseModuleActivationTests.Probe.Create<IPromotionService>();
        var probe = EnterpriseModuleActivationTests.Probe.Of(promotions);
        probe.Handler = (_, _) => Task.FromException<List<CartItemDto>>(new IOException("private synthetic diagnostic"));
        state.PromotionService = promotions;
        var vm = new CartViewModel(state, null!, null!, loadOnCreate: false);
        var view = new PosCartTicketView { DataContext = vm }; var window = new Window { Content = view };
        window.Show();
        try
        {
            int opened = 0; vm.OpenCheckoutRequested += () => opened++;
            await vm.CheckoutCommand.ExecuteAsync("Efectivo"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, opened);
            Assert.Contains("reintentar", view.FindControl<TextBlock>("CheckoutFeedback")!.Text);
            Assert.Contains("reintentar", view.FindControl<TextBlock>("PricingProgress")!.Text);
            Assert.DoesNotContain("private", state.PricingFeedback);
            var pending = new TaskCompletionSource<List<CartItemDto>>(); probe.Handler = (_, _) => pending.Task;
            var first = vm.CheckoutCommand.ExecuteAsync("Efectivo");
            Assert.False(vm.CheckoutCommand.CanExecute("Efectivo"));
            await vm.CheckoutCommand.ExecuteAsync("Tarjeta"); await vm.CheckoutCommand.ExecuteAsync("Tarjeta");
            Assert.Equal(2, probe.Calls.Count); Assert.True(state.IsPricing);
            Dispatcher.UIThread.RunJobs(); Assert.Contains("Actualizando", view.FindControl<TextBlock>("PricingProgress")!.Text);
            pending.SetResult([new("P", "Synthetic", 10m, .333, "kg") { AppliedDiscountAmount = .33m, AppliedPromotionId = "RULE" }]);
            await first; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, opened); Assert.Equal("Efectivo", vm.InitialPaymentMode);
            Assert.Equal(3.33m, state.Subtotal); Assert.Equal(3m, state.Total); Assert.Equal(.33m, state.DiscountTotal);
            Assert.Equal(string.Format("-${0:N2}", .33m), view.FindControl<TextBlock>("PromotionDiscountTotal")!.Text);
            Assert.Empty(view.FindControl<TextBlock>("PricingProgress")!.Text!); Assert.Empty(vm.CheckoutMessage);
        }
        finally { window.Close(); }
    }
}
