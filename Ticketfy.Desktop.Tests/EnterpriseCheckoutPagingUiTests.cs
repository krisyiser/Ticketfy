using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task CheckoutPaging_AllCommercialTabHeadersAreReachableInSmallWindow()
    {
        var model = EnterpriseControl(); var view = new CommercialControlView(); var window = Show(view, model);
        window.Width = 1024; window.Height = 680;
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible))
            {
                Assert.True(tab.IsEffectivelyVisible && tab.Bounds.Width > 0, $"Header '{tab.Header}' must be visible.");
                var point = tab.TranslatePoint(default, window)!.Value;
                Assert.True(point.X >= 0 && point.X + tab.Bounds.Width <= window.Bounds.Width,
                    $"Header '{tab.Header}' is outside the window: {point.X} + {tab.Bounds.Width} > {window.Bounds.Width}");
                Assert.InRange(point.Y, 0, window.Bounds.Height - tab.Bounds.Height);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CheckoutPaging_LatePageCannotRefillAChangedSearchOrClosedSession()
    {
        await SeedPendingCheckoutsAsync(205);
        var original = _options;
        foreach (bool clearSession in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool armed = false;
            var delay = new CheckoutReadObserver(async command =>
            {
                if (!armed || !command.CommandText.Contains("\"payment_method\"", StringComparison.Ordinal)) return;
                entered.TrySetResult(); await release.Task;
            });
            _options = new DbContextOptionsBuilder<AppDbContext>(original).AddInterceptors(delay).Options;
            var model = EnterpriseControl();
            await model.RefreshCommand.ExecuteAsync(null);
            model.PendingCheckoutSearch = "PENDING";
            await model.SearchPendingCheckoutsCommand.ExecuteAsync(null);
            armed = true;
            var pending = model.SearchPendingCheckoutsCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (clearSession) model.ClearDirectoryData();
            else model.PendingCheckoutSearch = "NOT-FOUND";
            release.SetResult(); await pending;
            Assert.Empty(model.PendingCheckouts); Assert.Null(model.PendingCheckout);
            Assert.Empty(model.PendingCheckoutPageLabel); Assert.False(model.HasNextPendingCheckouts);
            Assert.False(model.CheckoutFundsVerified); Assert.Empty(model.CheckoutBankStatus);
            if (clearSession) Assert.Empty(model.PendingCheckoutSearch);
        }
        _options = original;
    }

    [AvaloniaFact]
    public async Task CheckoutPaging_RefreshFailureAfterResolutionDoesNotAnnounceMonetaryFailure()
    {
        await Sales.BeginCheckoutAsync(Sale("RESOLVED-BEFORE-READ-FAILURE"));
        bool failPage = false;
        var fault = new CheckoutReadObserver(command =>
        {
            if (failPage && command.CommandText.Contains("\"payment_method\"", StringComparison.Ordinal) &&
                !command.CommandText.Contains("\"sale_payload\"", StringComparison.Ordinal))
                throw new IOException("synthetic read failure");
            return Task.CompletedTask;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(fault).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.PendingCheckout = Assert.Single(model.PendingCheckouts); model.CheckoutFundsVerified = true;
        model.Reason = "Efectivo comprobado"; failPage = true;
        await model.ResolveCheckoutCommand.ExecuteAsync(null);
        Assert.Contains("Operación registrada", model.Message); Assert.Contains("Resolución registrada", model.Summary);
        Assert.Contains("No se pudo actualizar", model.Summary); Assert.Contains("Consulta no disponible", model.PendingCheckoutPageLabel);
        Assert.Null(model.PendingCheckout); Assert.Empty(model.PendingCheckouts);
        await using var db = Db();
        Assert.Single(await db.Sales.ToListAsync()); Assert.Single(await db.SalePayments.ToListAsync());
    }

    [AvaloniaFact]
    public async Task CheckoutPaging_RecoveryActionsRemainReachableInSmallWindow()
    {
        await SeedPendingCheckoutsAsync(1);
        using var client = new HttpClient(new EvidenceHttpHandler(_ => BankOrder()));
        var model = EnterpriseControl(BankTerminal(client)); var view = new CommercialControlView();
        var window = Show(view, model); window.Width = 1024; window.Height = 680;
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Recuperar cobros"));
            await Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            var resolve = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == model.ResolveCheckoutCommand);
            resolve.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            Assert.True(resolve.IsEffectivelyVisible); Assert.True(resolve.IsEffectivelyEnabled);
            var point = resolve.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.Y, 0, window.Bounds.Height - resolve.Bounds.Height);
            var scroll = resolve.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(scroll.Offset.Y > 0);
        }
        finally { window.Close(); }
    }
}
