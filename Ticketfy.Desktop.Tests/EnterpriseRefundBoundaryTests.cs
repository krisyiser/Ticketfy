using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundBoundary_LegacyListsCannotReturnAnUnboundedResult(bool credit)
    {
        await SeedRefundQueuesAsync(205, credit);
        if (credit) await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.GetPendingRefundsAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.GetPendingRefundsAsync());
    }

    [Fact]
    public async Task RefundBoundary_DepositReferenceCannotProveAnOutgoingRefund()
    {
        await SeedRefundQueuesAsync(1, false);
        await Reconciliation.ConfirmTransferAsync("UNRELATED-DEPOSIT", 10m, "BANK-IN");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "bank-in", "OUTGOING"));
        await using var db = Db();
        Assert.Empty(await db.RefundSettlements.ToListAsync());
        Assert.False(await db.TransactionJournal.AnyAsync(j => j.EventType == "EXTERNAL_REFUND_VERIFIED"));
        Assert.Single(await Reconciliation.GetPendingRefundsAsync());
    }

    [Fact]
    public async Task RefundBoundary_SettlementCannotReuseAnotherFinancialCommandsIdentity()
    {
        await SeedRefundQueuesAsync(1, false);
        await Sales.SaveAsync(Sale("OCCUPIED-IDENTITY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "REFUND-BANK", "OCCUPIED-IDENTITY"));
        await using var db = Db();
        Assert.Empty(await db.RefundSettlements.ToListAsync());
        Assert.True(await db.Sales.AnyAsync(s => s.OperationId == "OCCUPIED-IDENTITY"));
    }

    [Fact]
    public async Task RefundBoundary_MismatchedBankSettlementRemainsVisibleAndCannotBeAcknowledgedAsCorrect()
    {
        await SeedRefundQueuesAsync(1, false);
        await Reconciliation.SettleRefundAsync("REFUND-0000", "REFUND-BANK", "SETTLEMENT");
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("UPDATE refund_settlements SET Amount = 1;");
        Assert.Single(await Reconciliation.GetPendingRefundsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "REFUND-BANK", "SETTLEMENT"));
    }

    [Fact]
    public async Task RefundBoundary_ResolutionWithoutItsSettlementCannotReleaseReservationOrCloseShift()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Prueba", "INCONSISTENT-RESOLUTION");
        await using (var db = Db())
        {
            db.CreditRefundResolutions.Add(new()
            {
                RequestId = "INCONSISTENT-RESOLUTION",
                FundsMoved = true,
                Reason = "Synthetic old partial write",
                ApproverUserId = CommercialTestSession.UserId.ToString()
            });
            await db.SaveChangesAsync();
        }
        Assert.Single(await Credits.GetPendingRefundsAsync());
        Assert.Empty(await Credits.GetDocumentsAsync(AccountKind.Customer, "C"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 20m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("INCONSISTENT-RESOLUTION", true, null, "Revisión", true));
        await using var verify = Db();
        Assert.Empty(await verify.AccountCreditSettlements.ToListAsync());
        Assert.Equal(-10m, (await verify.Customers.SingleAsync()).Debt);
    }

    private async Task SeedRefundQueuesAsync(int count, bool credit)
    {
        await using var db = Db();
        string? paymentId = null;
        if (!credit)
        {
            CommercialTestSession.ConfirmCard(db, "QUEUE-SALE", 20m, "QUEUE-PAYMENT");
            await Sales.SaveAsync(Sale("QUEUE-SALE") with { PaymentMethod = "Tarjeta", CashAmount = 0m, CardAmount = 20m });
            paymentId = (await db.SalePayments.SingleAsync(p => p.OperationId == "QUEUE-SALE")).Id;
        }
        for (int i = 0; i < count; i++)
            if (credit) db.CreditRefundRequests.Add(new()
            {
                OperationId = $"REFUND-{i:D4}",
                AccountKind = AccountKind.Customer,
                AccountId = "C",
                SourceDocumentId = $"DOC-{i:D4}",
                Amount = 10m,
                TenderKind = TenderKind.Cash,
                CashierUserId = CommercialTestSession.UserId.ToString(),
                RegisterId = "TEST-REGISTER",
                ShiftId = "SHIFT-1",
                Reason = new string('n', 4096)
            });
            else db.PaymentRefunds.Add(new()
            {
                Id = $"REFUND-{i:D4}",
                OperationId = $"RETURN-OP-{i:D4}",
                ReturnId = $"RETURN-{i:D4}",
                PaymentId = paymentId!,
                Kind = TenderKind.Card,
                Amount = 10m,
                State = TenderState.RequiresReconciliation,
                CashierUserId = CommercialTestSession.UserId.ToString(),
                RegisterId = "TEST-REGISTER",
                ShiftId = "SHIFT-1"
            });
        await db.SaveChangesAsync();
    }
}
