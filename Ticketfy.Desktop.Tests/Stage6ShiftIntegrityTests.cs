using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private ShiftService ShiftsService() => new(new TestDbContextFactory(_options), _actor);

    // Deliberate corruption in an isolated EnsureCreated database, not a supported editing path.
    [Theory]
    [InlineData("UPDATE sale_payments SET State=1;")]
    [InlineData("UPDATE sale_payments SET RegisterId='OTHER';")]
    [InlineData("UPDATE sale_payments SET OperationId='UNRELATED';")]
    [InlineData("UPDATE sales SET CashierUserId='OTHER';")]
    public async Task DailyReconciliation_RejectsInvalidPaymentOwnershipStateOrDocument(string corruption)
    {
        await Sales.SaveAsync(Sale("VERIFY"));
        await ShiftsService().CloseAsync("SHIFT-1", 20m);
        Assert.True(Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today)).IsBalanced);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(corruption);
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Contains(report.Issues, issue => issue.Code == "PAYMENT_LEDGER");
    }

    [Fact]
    public async Task Close_RejectsProjectionDriftWithoutSilentlyRepairingCash()
    {
        await Sales.SaveAsync(Sale("DRIFT"));
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("UPDATE shifts SET expectedBalance=9900;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().CloseAsync("SHIFT-1", 20m));
        Assert.Equal(1, (await db.Shifts.SingleAsync()).IsOpen);
        Assert.Equal(99m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Empty(await db.Cashups.ToListAsync());
        Assert.DoesNotContain(await db.TransactionJournal.ToListAsync(), j => j.EventType == "SHIFT_CLOSED");
    }

    [Fact]
    public async Task DailyReconciliation_RejectsMissingCashupAndCloseEvidence()
    {
        await ShiftsService().CloseAsync("SHIFT-1", 0m);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM cashups;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM transaction_journal WHERE event_type='SHIFT_CLOSED';");
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Contains(report.Issues, issue => issue.Code == "CLOSE_EVIDENCE");
    }

    [Theory]
    [InlineData("UPDATE cashups SET theoretical_cash=100;", "CLOSE_EVIDENCE")]
    [InlineData("UPDATE shifts SET expectedBalance=100;", "CASH_PROJECTION")]
    [InlineData("UPDATE shifts SET totalCashSales=100;", "CLOSE_EVIDENCE")]
    [InlineData("UPDATE transaction_journal SET amount=100 WHERE event_type='SHIFT_CLOSED';", "CLOSE_EVIDENCE")]
    public async Task DailyReconciliation_ExplainsDriftWithoutMutatingStoredRecords(string corruption, string code)
    {
        await ShiftsService().CloseAsync("SHIFT-1", 0m);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(corruption);
        int journalCount = await db.TransactionJournal.CountAsync();
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Contains(report.Issues, issue => issue.Code == code);
        Assert.Equal(journalCount, await db.TransactionJournal.CountAsync());
    }

    [Fact]
    public async Task DailyReconciliation_DoesNotSilentlyHideShiftsWithInvalidDates()
    {
        await ShiftsService().CloseAsync("SHIFT-1", 0m);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("UPDATE shifts SET endTime='unreadable-date';");
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Contains(report.Issues, issue => issue.Code == "SHIFT_DATE_INVALID");
    }

    [Fact]
    public async Task DailyReconciliation_RejectsAnInterruptedCheckoutEvenWhenCashBalances()
    {
        await ShiftsService().CloseAsync("SHIFT-1", 0m);
        await using var db = Db();
        db.CheckoutOperations.Add(new CheckoutOperationEntity
        {
            OperationId = "PENDING-CLOSE",
            State = CheckoutOperationState.Started,
            ShiftId = "SHIFT-1"
        });
        await db.SaveChangesAsync();
        Assert.False(Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today)).IsBalanced);
    }

    [Theory]
    [InlineData(MovementType.RetiroEfectivo, true)]
    [InlineData(MovementType.IngresoExtraordinario, false)]
    [InlineData(MovementType.CancelacionGasto, false)]
    public async Task Close_RejectsCashMovementWithoutItsSourceEvidence(MovementType type, bool outflow)
    {
        await Sales.SaveAsync(Sale("CASH-SOURCE"));
        await using var db = Db();
        db.ShiftMovements.Add(new ShiftMovementEntity
        {
            ShiftId = "SHIFT-1",
            RegisterId = "TEST-REGISTER",
            CashierUserId = CommercialTestSession.UserId.ToString(),
            OperationId = "ORPHAN",
            ReferenceId = "ORPHAN",
            Amount = 5m,
            IsOutflow = outflow,
            MovementType = type
        });
        var shift = await db.Shifts.SingleAsync();
        shift.ExpectedBalance += outflow ? -5m : 5m;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().CloseAsync("SHIFT-1", shift.ExpectedBalance));
        Assert.Empty(await db.Cashups.ToListAsync());
    }

    [Fact]
    public async Task ExpenseCancellationInLaterShift_AndAuditedCashCommandsReconcileSeparately()
    {
        var factory = new TestDbContextFactory(_options);
        var expenses = new ExpenseService(factory, _actor);
        await Sales.SaveAsync(Sale("EXP-CASH"));
        await expenses.CreateAsync(new ExpenseDto("EXP", "General", 5m, "", "Material", "Efectivo", "", OperationId: "EXP-OP"));
        await ShiftsService().CloseAsync("SHIFT-1", 15m);
        var next = await ShiftsService().OpenAsync(15m, "SHIFT-2-OPEN");
        await expenses.CancelAsync("EXP", "Dinero recuperado", "VOID-EXP", true);
        await ShiftsService().RegisterMovementAsync(10m, false, "Fondo adicional", "EXTRA");
        await ShiftsService().RegisterMovementAsync(5m, true, "Retiro a caja fuerte", "DROP");
        await ShiftsService().CloseAsync(next.Id, 25m);
        var reports = await Reconciliation.ReconcileAsync(DateTime.Today);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.True(report.IsBalanced));
    }

    [Fact]
    public async Task Close_RejectsCashDirectionChangedWithoutChangingItsCommandEvidence()
    {
        await Sales.SaveAsync(Sale("DIRECTION"));
        await ShiftsService().RegisterMovementAsync(5m, true, "Retiro", "DROP");
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("UPDATE shift_movements SET is_outflow=0, movement_type=8 WHERE OperationId='DROP';");
        await db.Database.ExecuteSqlRawAsync("UPDATE shifts SET expectedBalance=2500;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().CloseAsync("SHIFT-1", 25m));
    }

    [Fact]
    public async Task DailyReconciliation_RequiresFullExternalRefundSettlementAmount()
    {
        await Reconciliation.ConfirmTransferAsync("REFUND-AMOUNT", 20m, "DEPOSIT-AMOUNT");
        var sale = await Sales.SaveAsync(Sale("REFUND-AMOUNT") with { CashAmount = 0m, PaymentMethod = "Transferencia SPEI" });
        await Sales.CancelSaleAsync(sale.Id, "Devuelto");
        var pending = Assert.Single(await Reconciliation.GetPendingRefundsAsync());
        await Reconciliation.SettleRefundAsync(pending.Id, "REFUND-AMOUNT-BANK", "SETTLE-AMOUNT");
        await ShiftsService().CloseAsync("SHIFT-1", 0m);
        Assert.True(Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today)).IsBalanced);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("UPDATE refund_settlements SET Amount=1000;");
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Equal(20m, report.PendingRefunds);
        Assert.Contains(report.Issues, issue => issue.Code == "PENDING_REFUND");
    }

    [Fact]
    public async Task ZeroTotalSaleAndCashDifference_PreserveValidDocumentsAndExplainOnlyTheDifference()
    {
        await Sales.SaveAsync(Sale("ZERO-SALE", 0m));
        await ShiftsService().CloseWithCashupAsync("SHIFT-1", new CashupEntity
        {
            Id = "COUNT",
            OperationId = "ZERO-CLOSE",
            Type = "Partial",
            ClosedCashAmount = 1m
        });
        var report = Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today));
        Assert.False(report.IsBalanced);
        Assert.Equal("CASH_DIFFERENCE", Assert.Single(report.Issues).Code);
        await using var db = Db();
        Assert.Empty(await db.SalePayments.ToListAsync());
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        Assert.Equal("Final", (await db.Cashups.SingleAsync()).Type);
    }

    [AvaloniaFact]
    public async Task Ui_ReconciliationShowsReasonsAndActualRegisterAndUserInsteadOfTotalsAsIdentity()
    {
        var factory = new TestDbContextFactory(_options);
        var vm = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor), new PurchaseService(factory, _actor),
            ShiftsService(), new GiftcardService(factory, _actor), Reconciliation, factory, _actor, Sales);
        var window = Show(new CommercialControlView(), vm);
        try
        {
            await vm.ReconcileCommand.ExecuteAsync(null);
            var row = Assert.Single(vm.Rows);
            Assert.Contains("sigue abierto", row.Event);
            Assert.Equal(CommercialTestSession.UserId.ToString(), row.User);
            Assert.Equal("TEST-REGISTER", row.Register);
            await ShiftsService().CloseAsync("SHIFT-1", 0m);
            await vm.ReconcileCommand.ExecuteAsync(null);
            Assert.Equal("CONCILIADO INTERNO", Assert.Single(vm.Rows).Event);
        }
        finally { window.Close(); }
    }
}
