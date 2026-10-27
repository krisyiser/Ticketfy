using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Services.Interfaces;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task TerminalEvidence_BoundLookupDoesNotChargeAndRequiresSeparateSupervisedResolution()
    {
        await PrepareTerminalCheckout();
        var handler = new EvidenceHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException("Lost creation response");
            return BankOrder();
        });
        using var client = new HttpClient(handler);
        var service = BankTerminal(client);
        await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        await Sales.MarkCheckoutFailedAsync("BANK-EVIDENCE", "Synthetic uncertainty", true);
        var model = EnterpriseControl(service);
        var view = new CommercialControlView(); var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Recuperar cobros"));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            model.PendingCheckout = Assert.Single(model.PendingCheckouts);
            Assert.True(model.CanQueryCheckoutBank);
            var field = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), t => t.MaxLength == 64);
            field.Text = "ORD-EVIDENCE";
            Assert.Equal("ORD-EVIDENCE", model.CheckoutProviderOrderId);
            await ClickTwiceAsync(view, model.QueryCheckoutBankCommand);
            Assert.Contains("Pago bancario confirmado", model.CheckoutBankStatus);
            Assert.False(model.CheckoutFundsVerified);
            await using var db = Db();
            Assert.Empty(await db.Sales.ToListAsync());
            model.CheckoutFundsVerified = true; model.Reason = "Evidencia revisada por supervisor";
            await ClickTwiceAsync(view, model.ResolveCheckoutCommand);
            Assert.Single(await db.Sales.ToListAsync()); Assert.Single(await db.SalePayments.ToListAsync());
            Assert.Equal(1, handler.Methods.Count(m => m == HttpMethod.Post));
            Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TerminalEvidence_ChangingSelectionDiscardsLateLookupAndDoesNotCheckFundsAutomatically()
    {
        await PrepareTerminalCheckout();
        var response = new TaskCompletionSource<Result<TerminalReconciliationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = EnterpriseControl(new DeferredBankLookup(response.Task));
        await model.RefreshCommand.ExecuteAsync(null);
        model.PendingCheckout = Assert.Single(model.PendingCheckouts);
        var query = model.QueryCheckoutBankCommand.ExecuteAsync(null);
        model.PendingCheckout = null;
        response.SetResult(Result<TerminalReconciliationResult>.Success(new("BANK-EVIDENCE", "ORD-EVIDENCE", "Completed", true, false)));
        await query;
        Assert.Empty(model.CheckoutBankStatus); Assert.False(model.CheckoutFundsVerified);
        await using var db = Db(); Assert.Empty(await db.Sales.ToListAsync());
    }
    private sealed class DeferredBankLookup(Task<Result<TerminalReconciliationResult>> result) : IPaymentTerminalReconciliation
    {
        public Task<Result<TerminalReconciliationResult>> RefreshPaymentAsync(string referenceId, string? providerOrderId = null,
            CancellationToken cancellationToken = default) => result;
    }
}
