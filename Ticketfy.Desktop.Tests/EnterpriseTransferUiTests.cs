using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Services.Implementations;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task TransferUi_BoundPendingPagesSearchAndSelectionRemainConsistent()
    {
        await SeedTransferQueuesAsync(205);
        var model = EnterpriseControl(); var view = new CommercialControlView(); var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            view.GetVisualDescendants().OfType<TabControl>().Single().SelectedItem =
                view.GetVisualDescendants().OfType<TabControl>().Single().Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Transferencias");
            await Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            Assert.Equal(100, model.PendingTransfers.Count); Assert.Contains("205", model.TransferPageLabel);
            var old = model.PendingTransfers[0]; model.PendingTransfer = old; model.TransferFundsVerified = true;
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == model.NextTransfersCommand);
            Assert.True(next.IsEffectivelyEnabled);
            await model.NextTransfersCommand.ExecuteAsync(null);
            Assert.Equal("TRANSFER-0100", model.PendingTransfers[0].OperationId);
            Assert.Null(model.PendingTransfer); Assert.False(model.TransferFundsVerified);
            await model.NextTransfersCommand.ExecuteAsync(null);
            Assert.Equal(5, model.PendingTransfers.Count); Assert.False(model.HasNextTransfers);
            await model.PreviousTransfersCommand.ExecuteAsync(null);
            Assert.Equal("TRANSFER-0100", model.PendingTransfers[0].OperationId);
            model.PendingTransfer = old; model.TransferNoFundsVerified = true; model.Reason = "No fondos";
            await model.CancelUnfundedTransferCommand.ExecuteAsync(null);
            Assert.Contains("vigente", model.Message);
            var search = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                t => t.Watermark?.ToString()?.StartsWith("Buscar referencia local o destino", StringComparison.Ordinal) == true);
            search.Text = "0204";
            Assert.Empty(model.PendingTransfers); Assert.Null(model.PendingTransfer); Assert.False(model.TransferNoFundsVerified);
            await model.SearchTransfersCommand.ExecuteAsync(null);
            Assert.Equal("TRANSFER-0204", Assert.Single(model.PendingTransfers).OperationId);
            model.ClearDirectoryData();
            Assert.Empty(model.PendingTransfers); Assert.Empty(model.TransferSearch); Assert.Empty(model.TransferPageLabel);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TransferUi_HistoryTraversesAllReceiptsAndSwitchingModeClearsMoneySelection()
    {
        await SeedTransferQueuesAsync(205, true);
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        await model.TransferHistoryCommand.ExecuteAsync(null);
        Assert.True(model.TransferHistoryMode); Assert.Equal(100, model.Rows.Count);
        Assert.Contains("205", model.TransferPageLabel); Assert.Contains("todas las cajas", model.Summary);
        await model.NextTransfersCommand.ExecuteAsync(null);
        Assert.Equal(100, model.Rows.Count);
        await model.NextTransfersCommand.ExecuteAsync(null);
        Assert.Equal(5, model.Rows.Count); Assert.False(model.HasNextTransfers);
        model.TransferSearch = "BANK-0204";
        Assert.Empty(model.Rows); await model.SearchTransfersCommand.ExecuteAsync(null);
        Assert.Contains("BANK-0204", Assert.Single(model.Rows).Event);
        await model.PendingTransfersCommand.ExecuteAsync(null);
        Assert.False(model.TransferHistoryMode); Assert.Empty(model.Rows); Assert.Null(model.PendingTransfer);
    }

    [AvaloniaFact]
    public async Task TransferUi_LateReadsCannotRepopulateChangedSearchOrEndedSession()
    {
        var options = _options;
        await SeedTransferQueuesAsync(2);
        foreach (bool logout in new[] { false, true })
        {
            bool armed = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new TransferReadObserver(async command =>
            {
                if (!armed || !command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) return;
                entered.TrySetResult(); await release.Task;
            });
            _options = new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(observer).Options;
            var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
            armed = true;
            var read = model.SearchTransfersCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (logout) model.ClearDirectoryData(); else model.TransferSearch = "OTHER";
            release.SetResult(); await read;
            Assert.Empty(model.PendingTransfers); Assert.Empty(model.Rows); Assert.Empty(model.TransferPageLabel);
            Assert.False(model.HasNextTransfers); Assert.Null(model.PendingTransfer);
        }
        _options = options;
    }

    [AvaloniaFact]
    public async Task TransferUi_SupervisorDoubleClickAndFailedRefreshKeepOneAppliedDeposit()
    {
        await Transfers.PrepareAsync("UI-SUPERVISED", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync(); _actor = supervisor.Actor;
        bool fail = false;
        bool hold = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new TransferReadObserver(async command =>
        {
            if (hold && command.CommandText.Contains("\"Notes\"", StringComparison.Ordinal))
            {
                hold = false; entered.TrySetResult(); await release.Task;
            }
            if (fail && command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => p.Value is int limit && limit == 101))
                throw new IOException("synthetic refresh failure");
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.PendingTransfer = Assert.Single(model.PendingTransfers);
        model.TransferReference = "BANK-UI"; model.TransferFundsVerified = true; fail = true; hold = true;
        var first = model.ApplyTransferCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var duplicate = model.ApplyTransferCommand.ExecuteAsync(null);
        release.SetResult(); await Task.WhenAll(first, duplicate);
        Assert.Contains("Resolución registrada", model.Message);
        Assert.Contains("No se pudo actualizar", model.Summary); Assert.Contains("no vuelva a mover dinero", model.Summary);
        Assert.Empty(model.PendingTransfers); Assert.Null(model.PendingTransfer); Assert.False(model.TransferFundsVerified);
        Assert.False(model.ApplyTransferCommand.CanExecute(null));
        fail = false;
        await using var db = Db();
        Assert.Equal(110m, (await db.Giftcards.SingleAsync()).Balance);
        Assert.Single(await db.IncomingTransferReceipts.ToListAsync()); Assert.Single(await db.AccountEntries.ToListAsync());
        await model.SearchTransfersCommand.ExecuteAsync(null); Assert.Empty(model.PendingTransfers);
    }

    [AvaloniaFact]
    public async Task TransferUi_PreparedIntentSurvivesRefreshFailureWithoutLosingItsReference()
    {
        bool fail = false;
        var observer = new TransferReadObserver(command =>
        {
            if (fail && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
                throw new IOException("synthetic refresh failure");
            return Task.CompletedTask;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var model = EnterpriseControl(); await model.RefreshCommand.ExecuteAsync(null);
        model.TransferToWallet = true; model.WalletNumber = "WALLET"; model.Amount = 10m; model.Reason = "Prueba";
        fail = true; await model.PrepareTransferCommand.ExecuteAsync(null); fail = false;
        await using var db = Db();
        var saved = await db.IncomingTransferIntents.SingleAsync();
        Assert.Contains(saved.OperationId, model.Message); Assert.Equal(saved.OperationId, model.TransferSearch);
        Assert.Contains("No se pudo actualizar", model.Summary); Assert.Equal(0m, model.Amount);
        Assert.Empty(await db.TenderConfirmations.ToListAsync()); Assert.Empty(await db.IncomingTransferReceipts.ToListAsync());
        await model.SearchTransfersCommand.ExecuteAsync(null);
        Assert.Equal(saved.OperationId, Assert.Single(model.PendingTransfers).OperationId);
    }
}
