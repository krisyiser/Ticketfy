using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task RefundUi_BothQueuesHaveBoundNavigationSearchAndSelectionSafety()
    {
        await SeedRefundQueuesAsync(205, true); await SeedRefundQueuesAsync(205, false);
        var model = EnterpriseControl(); var view = new CommercialControlView(); var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(100, model.PendingCreditRefunds.Count); Assert.Equal(100, model.PendingRefunds.Count);
            Assert.Contains("205", model.CreditRefundPageLabel); Assert.Contains("205", model.BankRefundPageLabel);
            foreach (bool credit in new[] { true, false })
            {
                var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == (credit ? "Saldos a favor" : "Conciliación"));
                await Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
                var next = credit ? model.NextCreditRefundsCommand : model.NextBankRefundsCommand;
                Assert.True(Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == next).IsEffectivelyEnabled);
                if (credit) { model.PendingCreditRefund = model.PendingCreditRefunds[0]; model.CreditRefundFundsVerified = true; }
                else { model.Refund = model.PendingRefunds[0]; model.ExternalRefundVerified = true; }
                model.Reference = "STALE-REFERENCE";
                Assert.False(model.CreditRefundFundsVerified); Assert.False(model.ExternalRefundVerified);
                await next.ExecuteAsync(null);
                Assert.Empty(model.Reference);
                if (credit) { Assert.Null(model.PendingCreditRefund); Assert.False(model.CreditRefundFundsVerified); }
                else { Assert.Null(model.Refund); Assert.False(model.ExternalRefundVerified); }
                await next.ExecuteAsync(null);
                Assert.Equal(5, credit ? model.PendingCreditRefunds.Count : model.PendingRefunds.Count);
                Assert.False(credit ? model.HasNextCreditRefunds : model.HasNextBankRefunds);
                await (credit ? model.PreviousCreditRefundsCommand : model.PreviousBankRefundsCommand).ExecuteAsync(null);
                Assert.Equal("REFUND-0100", credit ? model.PendingCreditRefunds[0].OperationId : model.PendingRefunds[0].Id);
                var search = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), t =>
                    t.Watermark?.ToString()?.StartsWith(credit ? "Buscar solicitud," : "Buscar reembolso,", StringComparison.Ordinal) == true);
                search.Text = "0204";
                await (credit ? model.SearchCreditRefundsCommand : model.SearchBankRefundsCommand).ExecuteAsync(null);
                Assert.Equal("REFUND-0204", credit ? Assert.Single(model.PendingCreditRefunds).OperationId : Assert.Single(model.PendingRefunds).Id);
            }
            model.ClearDirectoryData();
            Assert.Empty(model.PendingRefunds); Assert.Empty(model.PendingCreditRefunds);
            Assert.Empty(model.BankRefundPageLabel); Assert.Empty(model.CreditRefundPageLabel);
            Assert.False(model.RefundAccountCreditCommand.CanExecute(null)); Assert.False(model.SettleRefundCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RefundUi_LateReadCannotRestoreRowsAfterSearchOrSessionChange()
    {
        await SeedRefundQueuesAsync(2, true); await SeedRefundQueuesAsync(2, false);
        var original = _options;
        foreach (bool credit in new[] { true, false })
            foreach (bool logout in new[] { true, false })
            {
                bool armed = false;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var observer = new RefundReadObserver(async command =>
                {
                    if (!armed || !command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) return;
                    entered.TrySetResult(); await release.Task;
                });
                _options = new DbContextOptionsBuilder<AppDbContext>(original).AddInterceptors(observer).Options;
                var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null); armed = true;
                var read = (credit ? model.SearchCreditRefundsCommand : model.SearchBankRefundsCommand).ExecuteAsync(null);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (logout) model.ClearDirectoryData();
                else if (credit) model.CreditRefundSearch = "OTHER"; else model.BankRefundSearch = "OTHER";
                release.SetResult(); await read;
                Assert.Empty(credit ? model.CreditRefundPageLabel : model.BankRefundPageLabel);
                Assert.Equal(0, credit ? model.PendingCreditRefunds.Count : model.PendingRefunds.Count);
            }
        _options = original;
    }

    [AvaloniaFact]
    public async Task RefundUi_BankDoubleClickAndPostCommitReadFailureDoNotRepeatSettlement()
    {
        await SeedRefundQueuesAsync(1, false);
        bool fail = false, hold = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new RefundReadObserver(async command =>
        {
            if (hold && !command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
            { hold = false; entered.TrySetResult(); await release.Task; }
            if (fail && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
                throw new IOException("synthetic list failure after commit");
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.Refund = Assert.Single(model.PendingRefunds); model.Reference = "BANK-OUT"; model.ExternalRefundVerified = true;
        hold = fail = true;
        var first = model.SettleRefundCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = model.SettleRefundCommand.ExecuteAsync(null);
        release.SetResult(); await Task.WhenAll(first, second); fail = false;
        Assert.Contains("conciliado", model.Message); Assert.Contains("No se pudo actualizar", model.Summary);
        Assert.Contains("no vuelva a mover dinero", model.Summary); Assert.Null(model.Refund);
        await using var db = Db();
        Assert.Single(await db.RefundSettlements.ToListAsync());
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "EXTERNAL_REFUND_VERIFIED").ToListAsync());
    }

    [AvaloniaFact]
    public async Task RefundUi_CreditPrepareAndResolvePreserveSuccessDespiteRefreshFailure()
    {
        await PaidCustomerReturnAsync();
        bool fail = false;
        var observer = new RefundReadObserver(command =>
        {
            if (fail && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
                throw new IOException("synthetic list failure");
            return Task.CompletedTask;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.Customer = Assert.Single(model.Customers); await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
        model.CreditSource = Assert.Single(model.CreditSources); model.Amount = 10m; model.Method = "Efectivo";
        model.Reason = "Prueba"; model.CreditActionVerified = true; fail = true;
        await model.PrepareAccountCreditRefundCommand.ExecuteAsync(null); fail = false;
        await using var db = Db(); var request = await db.CreditRefundRequests.SingleAsync();
        Assert.Contains(request.OperationId, model.Message); Assert.Contains("Solicitud guardada", model.Message);
        Assert.Contains("No se pudo actualizar", model.Summary); Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        await model.LoadCreditRefundsCommand.ExecuteAsync(null);
        model.PendingCreditRefund = Assert.Single(model.PendingCreditRefunds);
        model.CreditRefundFundsVerified = true; fail = true;
        await model.RefundAccountCreditCommand.ExecuteAsync(null); fail = false;
        Assert.Contains("Resolución registrada", model.Message); Assert.Contains("No se pudo actualizar", model.Summary);
        Assert.Null(model.PendingCreditRefund); Assert.False(model.CreditRefundFundsVerified);
        Assert.Single(await db.AccountCreditSettlements.ToListAsync());
        Assert.Equal(0m, (await db.Customers.SingleAsync()).Debt);
    }

    [AvaloniaFact]
    public async Task RefundUi_InconsistentHistoricalRowsRemainVisibleButDisableMoneyCommands()
    {
        await SeedRefundQueuesAsync(1, true); await SeedRefundQueuesAsync(1, false);
        await Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-OUT", "REFUND-COMMAND");
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE refund_settlements SET Amount = 1;");
            db.CreditRefundResolutions.Add(new() { RequestId = "REFUND-0000", FundsMoved = true, ApproverUserId = "SYNTHETIC" });
            await db.SaveChangesAsync();
        }
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.Refund = Assert.Single(model.PendingRefunds); model.PendingCreditRefund = Assert.Single(model.PendingCreditRefunds);
        Assert.Contains("INCONSISTENCIA", model.Refund.Label); Assert.Contains("INCONSISTENCIA", model.PendingCreditRefund.Label);
        Assert.False(model.SettleRefundCommand.CanExecute(null));
        Assert.False(model.RefundAccountCreditCommand.CanExecute(null)); Assert.False(model.CancelAccountCreditRefundCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RefundUi_SessionInvalidationAfterPrepareDoesNotStartAnotherListRead()
    {
        await PaidCustomerReturnAsync();
        bool armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new RefundReadObserver(async command =>
        {
            // This bounded balance read occurs AFTER preparation commits.
            if (!armed || !command.CommandText.Contains("Account credit document totals", StringComparison.Ordinal)) return;
            armed = false; entered.TrySetResult(); await release.Task;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.Customer = Assert.Single(model.Customers); await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
        model.CreditSource = Assert.Single(model.CreditSources); model.Amount = 10m; model.Reason = "Prueba";
        model.CreditActionVerified = true; armed = true;
        var prepare = model.PrepareAccountCreditRefundCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        model.ClearDirectoryData(); release.SetResult(); await prepare;
        Assert.Empty(model.PendingCreditRefunds); Assert.Empty(model.CreditSources);
        Assert.Empty(model.Message); Assert.Empty(model.Summary); Assert.Empty(model.CreditRefundPageLabel);
        await using var db = Db(); Assert.Single(await db.CreditRefundRequests.ToListAsync());
    }
}
