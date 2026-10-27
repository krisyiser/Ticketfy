using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("amount")]
    [InlineData("account")]
    [InlineData("cash")]
    [InlineData("ledger")]
    [InlineData("cancel")]
    public async Task RefundIntegrity_InconsistentResolutionKeepsRecoveryAndCloseBlocked(string damage)
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Prueba", "CORRUPT");
        await Credits.ResolveRefundAsync("CORRUPT", true, null, "Entregado", true);
        await using var db = Db();
        string sql = damage switch
        {
            "amount" => "UPDATE account_credit_settlements SET Amount = 1;",
            "account" => "UPDATE account_credit_settlements SET AccountId = 'OTHER';",
            "cash" => "DELETE FROM shift_movements WHERE OperationId = 'CORRUPT';",
            "ledger" => "DELETE FROM account_entries WHERE OperationId = 'CORRUPT';",
            _ => "UPDATE credit_refund_resolutions SET FundsMoved = 0;"
        };
        await db.Database.ExecuteSqlRawAsync(sql);
        Assert.True(Assert.Single((await Credits.GetPendingRefundsPageAsync()).Items).RequiresReview);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("CORRUPT", damage != "cancel", null, "revisión", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 10m));
        Assert.Single(await db.AccountCreditSettlements.ToListAsync());
        Assert.Single(await db.CreditRefundResolutions.ToListAsync());
    }

    [Fact]
    public async Task RefundIntegrity_MissingResolutionCannotCancelExistingMoney()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Prueba", "PARTIAL");
        await Credits.ResolveRefundAsync("PARTIAL", true, null, "Entregado", true);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM credit_refund_resolutions;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("PARTIAL", false, null, "No fondos", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("PARTIAL", true, null, "Fondos", true));
        Assert.True(Assert.Single((await Credits.GetPendingRefundsPageAsync()).Items).RequiresReview);
        Assert.Single(await db.AccountCreditSettlements.ToListAsync());
    }

    [Fact]
    public async Task RefundIntegrity_BankSettlementRollsBackEverythingIfJournalFailsAndReplaysOnce()
    {
        await SeedRefundQueuesAsync(1, false);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_refund_journal BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type = 'EXTERNAL_REFUND_VERIFIED'
            BEGIN SELECT RAISE(ABORT, 'synthetic journal failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-OUT", "REFUND-COMMAND"));
        Assert.Empty(await db.RefundSettlements.ToListAsync());
        Assert.False(await db.CommandReceipts.AnyAsync(c => c.OperationId == "REFUND-COMMAND"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_refund_journal;");
        await Reconciliation.SettleRefundAsync("REFUND-0000", " bank-out ", " REFUND-COMMAND ");
        await Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-OUT", "REFUND-COMMAND");
        Assert.Empty(await Reconciliation.GetPendingRefundsAsync());
        Assert.Single(await db.RefundSettlements.ToListAsync());
        Assert.Single(await db.CommandReceipts.Where(c => c.OperationId == "REFUND-COMMAND").ToListAsync());
        Assert.Single(await db.TransactionJournal.Where(c => c.EventType == "EXTERNAL_REFUND_VERIFIED").ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "OTHER", "REFUND-COMMAND"));
    }

    [Fact]
    public async Task RefundIntegrity_OriginalCardChargeIsNotRefundEvidence()
    {
        await SeedRefundQueuesAsync(1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "queue-payment", "REFUND"));
        await using var db = Db();
        Assert.Empty(await db.RefundSettlements.ToListAsync());
    }

    [Fact]
    public async Task RefundIntegrity_OutgoingRefundReferenceIsNotANewDeposit()
    {
        await SeedRefundQueuesAsync(1, false);
        await Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-OUT", "REFUND-COMMAND");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.ConfirmTransferAsync("ANOTHER-DEPOSIT", 10m, "bank-out"));
        await using var db = Db();
        Assert.False(await db.TenderConfirmations.AnyAsync(c => c.OperationId == "ANOTHER-DEPOSIT"));
    }

    [Fact]
    public async Task RefundIntegrity_AnotherDepositCannotProveTheRefundOfATransfer()
    {
        await Transfers.PrepareAsync("TO-REFUND", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        await Reconciliation.ConfirmTransferAsync("TO-REFUND", 10m, "DEPOSIT-1", IncomingTransferService.WalletPurpose);
        await Reconciliation.ConfirmTransferAsync("UNRELATED", 10m, "DEPOSIT-2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ResolveByExternalRefundAsync("TO-REFUND", "deposit-2", "Prueba", true));
        await using var db = Db();
        Assert.Empty(await db.IncomingTransferReceipts.ToListAsync());
        Assert.Single((await Transfers.GetPendingPageAsync()).Items);
    }

    [Theory]
    [InlineData("deposit")]
    [InlineData("refund")]
    [InlineData("supplier")]
    public async Task RefundIntegrity_SupplierPaymentCannotReuseABankTransaction(string source)
    {
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var purchase = await purchases.RegisterPurchaseAsync(new PurchaseDto("BUY-REF", "S", "Proveedor", "F1", "", 0, "",
            [new("L", "BUY-REF", "P", "Producto", 4m, 3, 12m)], PaymentMethod: "Efectivo", PaidAmount: 0m));
        if (source == "deposit") await Reconciliation.ConfirmTransferAsync("DEPOSIT", 4m, "BANK-USED");
        else if (source == "refund")
        {
            await SeedRefundQueuesAsync(1, false);
            await Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-USED", "SETTLED");
        }
        else
        {
            await purchases.PayAsync(purchase.Id, 4m, "Transferencia", "BANK-USED", "PAY-1");
            await purchases.PayAsync(purchase.Id, 4m, "Transferencia", "BANK-USED", "PAY-1");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => purchases.PayAsync(purchase.Id, 4m, "Transferencia", "bank-used", "PAY-2"));
        await using var db = Db();
        Assert.Equal(source == "supplier" ? 1 : 0, await db.SupplierPayments.CountAsync());
        Assert.False(await db.CommandReceipts.AnyAsync(c => c.OperationId == "PAY-2"));
    }
}
