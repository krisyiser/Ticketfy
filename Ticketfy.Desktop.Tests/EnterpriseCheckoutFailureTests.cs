using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CheckoutFailure_AnotherIntentOrDirectSaleCannotBypassPendingSameShiftButOtherRegisterWorks()
    {
        await Sales.BeginCheckoutAsync(Sale("ONE-PENDING"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("SECOND-INTENT")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(Sale("BYPASS")));
        await Sales.BeginCheckoutAsync(Sale("ONE-PENDING"));
        await using var db = Db();
        var other = SalesFor(CommercialTestSession.Create(db, register: "OTHER-CHECKOUT", userId: Guid.NewGuid()));
        await other.SaveAsync(Sale("OTHER-REGISTER-SALE"));
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(18d, (await db.Products.SingleAsync()).Stock);
        Assert.Single(await db.CheckoutOperations.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().CloseAsync("SHIFT-1", 0m));
        await Sales.ResolveInterruptedCheckoutAsync("ONE-PENDING", false, true, "No hubo entrega de efectivo");
        await Sales.BeginCheckoutAsync(Sale("RELEASED"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutFailure_HistoricalFailedBankIntentRemainsVisibleAndBlocksClose(bool transfer)
    {
        var sale = Sale("OLD-FAILED") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" };
        await Sales.BeginCheckoutAsync(sale);
        await using var db = Db();
        (await db.CheckoutOperations.SingleAsync()).State = CheckoutOperationState.Failed;
        if (transfer)
            db.TenderConfirmations.Add(new TenderConfirmationEntity { OperationId = sale.Id, Amount = 20m, ExternalReference = "SYNTHETIC-TRANSFER", Kind = TenderKind.Transfer });
        else CommercialTestSession.ConfirmCard(db, sale.Id, 20m);
        await db.SaveChangesAsync();
        Assert.Single(await Sales.GetInterruptedCheckoutsAsync());
        Assert.True(await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, "SHIFT-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync(sale.Id, false, true, "No puede ignorar banco"));
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Empty(await verify.Sales.ToListAsync());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    public async Task CheckoutFailure_UnreadablePayloadCannotBeDeclaredUnfunded(string payload)
    {
        await Sales.BeginCheckoutAsync(Sale("INVALID-PAYLOAD"));
        await using var db = Db();
        (await db.CheckoutOperations.SingleAsync()).SalePayload = payload;
        await db.SaveChangesAsync();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation,
            await Sales.MarkCheckoutFailedAsync("INVALID-PAYLOAD", "Invalid legacy data", false));
    }

    [Fact]
    public async Task CheckoutFailure_VerifiedNoFundsIsStableAndCannotBeAuthorizedAgain()
    {
        await Sales.BeginCheckoutAsync(Sale("CLOSED-INTENT"));
        await Sales.ResolveInterruptedCheckoutAsync("CLOSED-INTENT", false, true, "No se recibió dinero");
        Assert.Equal(CheckoutOperationState.Failed, await Sales.MarkCheckoutFailedAsync("CLOSED-INTENT", "Late callback", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("CLOSED-INTENT")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.MarkPaymentAuthorizedAsync("CLOSED-INTENT", "SYNTHETIC"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("CLOSED-INTENT", true, true, "Cambiar la resolución"));
        Assert.Null(await Sales.MarkCheckoutFailedAsync("ABSENT-INTENT", "Nothing persisted", false));
    }

    [Fact]
    public async Task CheckoutFailure_CommitThenLateErrorCannotRegressCompletedOrDuplicateMoney()
    {
        await Sales.BeginCheckoutAsync(Sale("COMMITTED-FIRST"));
        var sale = await Sales.SaveAsync(Sale("COMMITTED-FIRST"));
        Assert.Equal(CheckoutOperationState.Completed, await Sales.MarkCheckoutFailedAsync("COMMITTED-FIRST", "Lost response", true));
        Assert.Equal(sale.Id, (await Sales.SaveAsync(Sale("COMMITTED-FIRST"))).Id);
        await using var db = Db();
        Assert.Single(await db.Sales.ToListAsync()); Assert.Single(await db.SalePayments.ToListAsync());
        Assert.Single(await db.InventoryMovements.ToListAsync());
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_LostCommitResponseIsRecognizedWithoutPrintingOrChargingAgain()
    {
        var failure = new LoseCheckoutCommitResponse();
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(failure).Options;
        var print = new TestPrintDispatcher(); int notified = 0;
        var terminal = new CheckoutFailureTerminal((_, _, _) => throw new InvalidOperationException("Cash must not call bank"));
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), print, terminal, UiCart(), 20m,
            () => { notified++; return Task.CompletedTask; })
        { ReceivedAmount = 20m };
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Contains("Venta registrada", vm.ErrorMessage);
        Assert.False(vm.RequiresReconciliation); Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(1, notified); Assert.Equal(0, print.Calls);
        await using var verify = Db();
        Assert.Single(await verify.Sales.ToListAsync()); Assert.Single(await verify.SalePayments.ToListAsync());
        Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_LostCommitWithFailedUiRefreshStillReportsConfirmedSale()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(new LoseCheckoutCommitResponse()).Options;
        int notified = 0, closed = 0;
        var print = new TestPrintDispatcher();
        var terminal = new CheckoutFailureTerminal((_, _, _) => throw new InvalidOperationException("Cash must not call bank"));
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), print, terminal, UiCart(), 20m,
            () => { notified++; throw new IOException("Synthetic failed history refresh"); })
        { ReceivedAmount = 20m };
        vm.RequestClose += () => closed++;
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Contains("Venta registrada", vm.ErrorMessage);
        Assert.DoesNotContain("No se pudo confirmar", vm.ErrorMessage);
        Assert.False(vm.RequiresReconciliation);
        Assert.False(vm.CanEditTender);
        Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(1, notified); Assert.Equal(1, closed);
        Assert.Equal(0, print.Calls); Assert.Equal(0, terminal.Calls);
        await using var verify = Db();
        Assert.Single(await verify.Sales.ToListAsync());
        Assert.Single(await verify.SalePayments.ToListAsync());
        Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_UnrecordableFailureKeepsReferenceAndCannotStartASecondPayment()
    {
        var terminal = new CheckoutFailureTerminal(async (_, _, _) =>
        {
            await using var db = Db();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_checkout_status BEFORE UPDATE ON checkout_operations BEGIN SELECT RAISE(ABORT, 'synthetic journal failure'); END;");
            throw new IOException("Unknown bank response");
        });
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        string reference = vm.OperationReference;
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.True(vm.RequiresReconciliation); Assert.False(vm.CanEditTender);
        Assert.Contains("No se pudo confirmar el estado local", vm.ErrorMessage);
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(reference, vm.OperationReference); Assert.Equal(1, terminal.Calls);
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.Started, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.True(await ShiftLedgerValidator.HasPendingCheckoutsAsync(verify, "SHIFT-1"));
    }

    [Fact]
    public async Task CheckoutFailure_WalConcurrentFailureWaitsForCommittedSaleAndCannotOverwriteIt()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, actor) =>
        {
            await using (var setup = new AppDbContext(options))
            {
                setup.Products.Add(new ProductEntity { Id = "P", Name = "Producto", Barcode = "P", Price = 10m, Cost = 3m, Stock = 20 });
                await setup.SaveChangesAsync();
            }
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delaying = new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(new PauseCheckoutSave(saved, release)).Options;
            var ordinary = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), actor);
            await ordinary.BeginCheckoutAsync(Sale("CONCURRENT-FAILURE"));
            var writer = new SaleService(new TestDbContextFactory(delaying), new CoOccurrenceQueue(), new SpyAuditService(), actor);
            var sale = Task.Run(() => writer.SaveAsync(Sale("CONCURRENT-FAILURE")));
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var failureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = Task.Run(async () =>
            {
                failureStarted.SetResult();
                return await ordinary.MarkCheckoutFailedAsync("CONCURRENT-FAILURE", "Concurrent callback", false);
            });
            try { await failureStarted.Task; Assert.False(failure.IsCompleted); }
            finally { release.TrySetResult(); }
            await sale.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(CheckoutOperationState.Completed, await failure.WaitAsync(TimeSpan.FromSeconds(30)));
            await using var verify = new AppDbContext(options);
            Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
            Assert.Single(await verify.Sales.ToListAsync());
            Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
        });
    }

    [Theory]
    [InlineData(IntegrationOperationStatus.Pending)]
    [InlineData(IntegrationOperationStatus.Completed)]
    public async Task CheckoutFailure_PersistedBankEvidenceCannotBeDowngradedByCallerFlag(IntegrationOperationStatus status)
    {
        var sale = Sale("FAILURE-BANK") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" };
        await Sales.BeginCheckoutAsync(sale);
        await using var db = Db();
        db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
        {
            Id = "BANK-FAILURE",
            ExternalReference = sale.Id,
            IdempotencyKey = "BANK-KEY",
            Amount = 20m,
            Status = status,
            ProviderPaymentId = status == IntegrationOperationStatus.Completed ? "BANK-CONFIRMED" : null
        });
        await db.SaveChangesAsync();
        await Sales.MarkCheckoutFailedAsync(sale.Id, "Failed local response", false);
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await db.CheckoutOperations.SingleAsync()).State);
        Assert.True(await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, "SHIFT-1"));
        Assert.Single(await Sales.GetInterruptedCheckoutsAsync());
        Assert.Empty(await db.Sales.ToListAsync());
    }

    [Fact]
    public async Task CheckoutFailure_CashAndExistingReconciliationCannotBeClearedByLateFailure()
    {
        await Sales.BeginCheckoutAsync(Sale("FAILURE-CASH"));
        await Sales.MarkCheckoutFailedAsync("FAILURE-CASH", "First unknown outcome", true);
        await Sales.MarkCheckoutFailedAsync("FAILURE-CASH", "Late error without evidence", false);
        await using var db = Db();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await db.CheckoutOperations.SingleAsync()).State);
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task CheckoutFailure_VerifiedNoFundsCannotBePostedAsANewSaleByReusingTheOldIntent()
    {
        var sale = Sale("FAILURE-DISMISSED");
        await Sales.BeginCheckoutAsync(sale);
        await Sales.ResolveInterruptedCheckoutAsync(sale.Id, false, true, "No se recibió efectivo");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale));
        await using var db = Db();
        Assert.Equal(CheckoutOperationState.Failed, (await db.CheckoutOperations.SingleAsync()).State);
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_UiUnexpectedErrorAfterBankAcceptanceKeepsReconciliationVisible()
    {
        var terminal = new CheckoutFailureTerminal((_, reference, _) =>
        {
            using var db = Db();
            CommercialTestSession.ConfirmCard(db, reference, 20m);
            throw new IOException("Synthetic failure after bank acceptance");
        });
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        await using var verify = Db();
        Assert.Equal(IntegrationOperationStatus.Completed, (await verify.PaymentTerminalOperations.SingleAsync()).Status);
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
        Assert.Empty(await verify.Sales.ToListAsync());
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_CancelingWaitDoesNotClaimTheBankCancelledOrDismissInflightWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new CheckoutFailureTerminal(async (_, _, _) =>
        {
            entered.SetResult(); await release.Task;
            return Result<PaymentResponse>.Failure("Resultado incierto de prueba");
        });
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        int closed = 0; vm.RequestClose += () => closed++;
        var payment = vm.ConfirmPaymentCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            vm.CancelCommand.Execute(null);
            Assert.Equal(0, closed);
            await vm.CancelTerminalPaymentCommand.ExecuteAsync(null);
            Assert.DoesNotContain("Cobro cancelado", vm.ErrorMessage);
            Assert.Contains("no confirma", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { release.TrySetResult(); await payment; }
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_DismissedDialogCannotFinalizePrintOrNotifyLateAcceptedPayment()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var terminal = new CheckoutFailureTerminal(async (_, reference, token) =>
        {
            observed = token; entered.SetResult(); await release.Task;
            using var db = Db(); CommercialTestSession.ConfirmCard(db, reference, 20m);
            return Result<PaymentResponse>.Success(new() { TransactionId = "TEST-BANK:" + reference });
        });
        var print = new TestPrintDispatcher(); int notified = 0;
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), print, terminal, UiCart(), 20m,
            () => { notified++; return Task.CompletedTask; })
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        var work = vm.ConfirmPaymentCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        vm.OnDismissed(); vm.OnDismissed();
        Assert.True(observed.IsCancellationRequested);
        release.SetResult(); await work;
        Assert.Equal(0, print.Calls); Assert.Equal(0, notified);
        Assert.False(vm.CanEditTender); Assert.False(vm.ConfirmPaymentCommand.CanExecute(null));
        await using var verify = Db();
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await verify.CheckoutOperations.SingleAsync()).State);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_MixedRejectionPreservesCashIntentAndFreezesPayloadAndReference()
    {
        var terminal = new CheckoutFailureTerminal((_, _, _) => Task.FromResult(Result<PaymentResponse>.Failure("Synthetic rejection")), false);
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Mixto", CashAmount = 10m, CardAmount = 10m };
        string original = vm.OperationReference;
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.True(vm.RequiresReconciliation); Assert.False(vm.CanEditTender);
        vm.PaymentMethod = "Efectivo"; vm.ReceivedAmount = 20m;
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Equal(original, vm.OperationReference);
        Assert.Equal(1, terminal.Calls);
        await using var db = Db();
        Assert.Equal(original, (await db.CheckoutOperations.SingleAsync()).OperationId);
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await db.CheckoutOperations.SingleAsync()).State);
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_BoundDialogShowsReferenceAndFreezesTenderWithoutHidingClose()
    {
        var terminal = new CheckoutFailureTerminal((_, _, _) => Task.FromResult(Result<PaymentResponse>.Failure("Respuesta bancaria incierta")));
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        var view = new CheckoutDialog { DataContext = vm };
        var window = new Window { Content = view, Width = 1024, Height = 680 };
        window.Show();
        try
        {
            var charge = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == vm.ConfirmPaymentCommand);
            Assert.True(charge.IsEffectivelyEnabled);
            charge.Command!.Execute(null);
            await vm.ConfirmPaymentCommand.ExecutionTask!;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.True(vm.RequiresReconciliation);
            Assert.False(charge.IsEffectivelyEnabled);
            var input = Assert.Single(view.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is StackPanel);
            Assert.False(((StackPanel)input.Content!).IsEffectivelyEnabled);
            var reference = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == $"Referencia local: {vm.OperationReference}");
            var close = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "CERRAR");
            Assert.True(close.IsEffectivelyEnabled);
            foreach (var control in new Control[] { reference, close, charge })
            {
                Assert.True(control.IsEffectivelyVisible);
                var position = control.TranslatePoint(default, window)!.Value;
                Assert.InRange(position.X, 0, window.Bounds.Width - control.Bounds.Width);
                Assert.InRange(position.Y, 0, window.Bounds.Height - control.Bounds.Height);
            }
            int closed = 0; vm.RequestClose += () => closed++;
            close.Command!.Execute(null);
            Assert.Equal(1, closed);
            Assert.Equal(1, terminal.Calls);
            await using var db = Db();
            Assert.Empty(await db.Sales.ToListAsync());
            Assert.True(await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, "SHIFT-1"));
        }
        finally { vm.OnDismissed(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CheckoutFailure_PureCardKnownRejectionMayStartANewReferenceWithoutMoney()
    {
        var terminal = new CheckoutFailureTerminal((_, _, _) => Task.FromResult(Result<PaymentResponse>.Failure("Rejected before funds")), false);
        var vm = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Tarjeta Débito/Crédito" };
        string first = vm.OperationReference;
        await vm.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.False(vm.RequiresReconciliation); Assert.True(vm.CanEditTender);
        Assert.NotEqual(first, vm.OperationReference);
        await using var db = Db();
        Assert.Equal(CheckoutOperationState.Failed, (await db.CheckoutOperations.SingleAsync()).State);
        Assert.Empty(await db.Sales.ToListAsync());
    }

    private sealed class PauseCheckoutSave(TaskCompletionSource saved, TaskCompletionSource release) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            saved.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class LoseCheckoutCommitResponse : DbTransactionInterceptor
    {
        private bool _failed;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!_failed && eventData.Context is AppDbContext db && db.ChangeTracker.Entries<SaleEntity>().Any())
            {
                _failed = true;
                throw new IOException("Synthetic lost commit acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CheckoutFailureTerminal(Func<decimal, string, CancellationToken, Task<Result<PaymentResponse>>> process, bool requiresReconciliation = true) : IPaymentTerminalService
    {
        public int Calls { get; private set; }
        public Task<Result<PaymentResponse>> ProcessPaymentAsync(decimal amount, string referenceId, CancellationToken cancellationToken)
        { Calls++; return process(amount, referenceId, cancellationToken); }
        public Task<Result> CancelPaymentIntentAsync(string referenceId) => throw new InvalidOperationException("A wait cancellation must not send a bank cancellation.");
        public Task<bool> RequiresReconciliationAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(requiresReconciliation);
    }
}
