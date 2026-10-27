using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Ticketfy.Core.Security;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task TransferSupervision_PendingIsVisibleToAuthorizedSupervisorWithoutOwnShift()
    {
        await Transfers.PrepareAsync("ORIGINAL-PENDING", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync();
        var pending = Assert.Single(await supervisor.Service.GetPendingAsync());
        Assert.Equal("ORIGINAL-PENDING", pending.OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_ApplyPreservesOriginalAttributionAndAuditsActualApprover(bool customer)
    {
        if (customer) await SeedDebtAsync();
        string purpose = customer ? IncomingTransferService.CustomerPurpose : IncomingTransferService.WalletPurpose;
        await Transfers.PrepareAsync("SUPERVISED-APPLY", purpose, customer ? "C" : "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync();
        // The operator can no longer sign in. Recovery must not require impersonation.
        await using (var db = Db())
        {
            (await db.Users.SingleAsync(u => u.Id == CommercialTestSession.UserId)).IsActive = false;
            await db.SaveChangesAsync();
        }
        await supervisor.Service.ConfirmAndApplyAsync("SUPERVISED-APPLY", "SYNTHETIC-RECEIPT", true);
        await supervisor.Service.ConfirmAndApplyAsync("SUPERVISED-APPLY", "SYNTHETIC-RECEIPT", true);
        await using var verify = Db();
        var receipt = await verify.IncomingTransferReceipts.SingleAsync();
        Assert.Equal(CommercialTestSession.UserId.ToString(), receipt.CashierUserId);
        Assert.Equal("SHIFT-1", receipt.ShiftId); Assert.Equal("TEST-REGISTER", receipt.RegisterId);
        Assert.Equal(10m, receipt.Amount);
        var proof = await verify.TenderConfirmations.SingleAsync();
        Assert.Equal(receipt.CashierUserId, proof.CashierUserId); Assert.Equal(receipt.ShiftId, proof.ShiftId);
        var approval = await verify.TransactionJournal.SingleAsync(j => j.EventType == "INCOMING_TRANSFER_APPLIED");
        Assert.Equal(supervisor.Id.ToString(), approval.CashierUserId);
        Assert.Equal("SHIFT-1", approval.ShiftId); Assert.Equal("TEST-REGISTER", approval.RegisterId);
        Assert.Equal(supervisor.Id.ToString(), (await verify.TransactionJournal.SingleAsync(
            j => j.EventType == "TRANSFER_VERIFIED_MANUALLY")).CashierUserId);
        Assert.Empty(await verify.ShiftMovements.ToListAsync()); Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Equal(customer ? 90m : 0m, (await verify.Customers.SingleAsync()).Debt);
        Assert.Equal(customer ? 100m : 110m, (await verify.Giftcards.SingleAsync()).Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_CancelOrExternalRefundPreservesEvidenceAndDoesNotMoveLocalFunds(bool funded)
    {
        await Transfers.PrepareAsync("SUPERVISED-RESOLVE", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        if (funded) await Reconciliation.ConfirmTransferAsync("SUPERVISED-RESOLVE", 10m, "DEPOSIT", IncomingTransferService.WalletPurpose);
        var supervisor = await TransferSupervisorAsync();
        for (int i = 0; i < 2; i++)
            if (funded) await supervisor.Service.ResolveByExternalRefundAsync("SUPERVISED-RESOLVE", "REFUND", "Verificado", true);
            else await supervisor.Service.CancelUnfundedAsync("SUPERVISED-RESOLVE", "Sin recepción comprobada", true);
        await using var db = Db();
        Assert.Empty(await supervisor.Service.GetPendingAsync());
        var journal = await db.TransactionJournal.SingleAsync(j => j.EventType ==
            (funded ? "INCOMING_TRANSFER_REFUNDED_EXTERNALLY" : "INCOMING_TRANSFER_CANCELLED"));
        Assert.Equal(supervisor.Id.ToString(), journal.CashierUserId); Assert.Equal("SHIFT-1", journal.ShiftId);
        Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance);
        Assert.Empty(await db.ShiftMovements.ToListAsync()); Assert.Empty(await db.AccountEntries.ToListAsync());
        if (funded)
        {
            var receipt = await db.IncomingTransferReceipts.SingleAsync();
            Assert.Equal(TransferOutcome.RefundedExternally, receipt.Outcome);
            Assert.Equal(CommercialTestSession.UserId.ToString(), receipt.CashierUserId);
        }
        else Assert.Empty(await db.TenderConfirmations.ToListAsync());
    }

    private async Task<(IncomingTransferService Service, Guid Id, CommercialOperationContext Actor)> TransferSupervisorAsync()
    {
        Guid id = Guid.NewGuid();
        await using var db = Db();
        var actor = CommercialTestSession.Create(db, false, userId: id);
        return (new(new TestDbContextFactory(_options), actor), id, actor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_RecoveryAndDestinationPermissionsAreIndependentlyRequired(bool recovery)
    {
        await Transfers.PrepareAsync("PERMISSION-CHECK", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var restricted = await RestrictedTransferActorAsync(recovery);
        var service = new IncomingTransferService(new TestDbContextFactory(_options), restricted);
        if (recovery) Assert.Single((await service.GetPendingPageAsync()).Items);
        else
        {
            Assert.Empty((await service.GetPendingPageAsync()).Items);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelUnfundedAsync("PERMISSION-CHECK", "No fondos", true));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveByExternalRefundAsync("PERMISSION-CHECK", "OUT", "Verificado", true));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CommercialReconciliationService(new TestDbContextFactory(_options), restricted)
                .ConfirmTransferAsync("PERMISSION-CHECK", 10m, "BANK", IncomingTransferService.WalletPurpose));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmAndApplyAsync("PERMISSION-CHECK", "BANK", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReceiptsPageAsync());
        await using var db = Db();
        Assert.Empty(await db.TenderConfirmations.ToListAsync()); Assert.Empty(await db.IncomingTransferReceipts.ToListAsync());
        Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance);
    }

    private async Task<CommercialOperationContext> RestrictedTransferActorAsync(bool recovery)
    {
        var factory = new TestDbContextFactory(_options);
        var settings = new SettingsService(factory, operations: _actor);
        await settings.SetAsync("CustomRolesList", "[\"TRANSFER_REVIEWER\"]");
        await settings.SetAsync("RolePermissions_TRANSFER_REVIEWER", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, bool>
        { [PermissionCatalog.PaymentsReconcile] = true, [PermissionCatalog.CheckoutRecover] = recovery }));
        var engine = new UserRolePermissionEngine(settings); var session = new SessionManager();
        await using var db = Db();
        var id = Guid.NewGuid();
        CommercialTestSession.Create(db, false, userId: id, role: "TRANSFER_REVIEWER");
        session.StartSession(engine.MapToModel(await db.Users.SingleAsync(u => u.Id == id)));
        return new(session, engine, "TEST-REGISTER");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("cashier")]
    [InlineData("register")]
    public async Task TransferSupervision_CannotPostIntoAClosedOrMisattributedOriginalShift(string defect)
    {
        await Transfers.PrepareAsync("INVALID-SHIFT", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync();
        await using (var db = Db())
        {
            var shift = await db.Shifts.SingleAsync();
            if (defect == "closed") shift.IsOpen = 0;
            else if (defect == "cashier") shift.CashierUserId = supervisor.Id.ToString();
            else shift.RegisterId = "OTHER";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Service.ConfirmAndApplyAsync("INVALID-SHIFT", "BANK", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Service.CancelUnfundedAsync("INVALID-SHIFT", "No fondos", true));
        await using var verify = Db();
        Assert.Empty(await verify.TenderConfirmations.ToListAsync()); Assert.Empty(await verify.IncomingTransferReceipts.ToListAsync());
        Assert.Equal(100m, (await verify.Giftcards.SingleAsync()).Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_ApprovalJournalFailureRollsBackApplicationButKeepsProof(bool customer)
    {
        if (customer) await SeedDebtAsync();
        await Transfers.PrepareAsync("APPROVAL-FAILURE", customer ? IncomingTransferService.CustomerPurpose : IncomingTransferService.WalletPurpose,
            customer ? "C" : "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync();
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_transfer_approval BEFORE INSERT ON transaction_journal
                WHEN NEW.event_type = 'INCOMING_TRANSFER_APPLIED'
                BEGIN SELECT RAISE(ABORT, 'synthetic full disk'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => supervisor.Service.ConfirmAndApplyAsync("APPROVAL-FAILURE", "BANK", true));
        await using (var db = Db())
        {
            Assert.Single(await db.TenderConfirmations.ToListAsync());
            Assert.Empty(await db.IncomingTransferReceipts.ToListAsync()); Assert.Empty(await db.CustomerPayments.ToListAsync());
            Assert.Equal(customer ? 100m : 0m, (await db.Customers.SingleAsync()).Debt);
            Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance);
            Assert.False(await db.AccountEntries.AnyAsync(e => e.OperationId == "APPROVAL-FAILURE"));
            Assert.False(await db.CommandReceipts.AnyAsync(e => e.OperationId == "APPROVAL-FAILURE"));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_transfer_approval;");
        }
        await supervisor.Service.ConfirmAndApplyAsync("APPROVAL-FAILURE", "BANK", true);
        await supervisor.Service.ConfirmAndApplyAsync("APPROVAL-FAILURE", "BANK", true);
        await using var verify = Db();
        Assert.Single(await verify.IncomingTransferReceipts.ToListAsync());
        Assert.Single(await verify.TransactionJournal.Where(j => j.EventType == "INCOMING_TRANSFER_APPLIED").ToListAsync());
    }

    [Fact]
    public async Task TransferSupervision_TwoApproversInWalCannotApplyOneDepositTwice()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, original) =>
        {
            CommercialOperationContext first, second;
            await using (var db = new AppDbContext(options))
            {
                db.Giftcards.Add(new() { Id = "G", CardNumber = "WALLET", Balance = 0m, IsActive = true });
                first = CommercialTestSession.Create(db, false, userId: Guid.NewGuid());
                second = CommercialTestSession.Create(db, false, userId: Guid.NewGuid());
            }
            var factory = new TestDbContextFactory(options);
            await new IncomingTransferService(factory, original).PrepareAsync("CONCURRENT-TRANSFER", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
            await Task.WhenAll(Task.Run(() => new IncomingTransferService(factory, first).ConfirmAndApplyAsync("CONCURRENT-TRANSFER", "BANK", true)),
                Task.Run(() => new IncomingTransferService(factory, second).ConfirmAndApplyAsync("CONCURRENT-TRANSFER", "BANK", true)));
            await using var verify = new AppDbContext(options);
            Assert.Equal(10m, (await verify.Giftcards.SingleAsync()).Balance);
            Assert.Single(await verify.TenderConfirmations.ToListAsync()); Assert.Single(await verify.IncomingTransferReceipts.ToListAsync());
            Assert.Single(await verify.AccountEntries.ToListAsync()); Assert.Empty(await verify.ShiftMovements.ToListAsync());
            Assert.Single(await verify.TransactionJournal.Where(j => j.EventType == "INCOMING_TRANSFER_APPLIED").ToListAsync());
        });
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("cancel")]
    [InlineData("refund")]
    public async Task TransferSupervision_SameResolutionCanBeRetriedAfterShiftClosure(string resolution)
    {
        await Transfers.PrepareAsync("CLOSED-RETRY", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var supervisor = await TransferSupervisorAsync();
        if (resolution == "refund")
            await Reconciliation.ConfirmTransferAsync("CLOSED-RETRY", 10m, "BANK", IncomingTransferService.WalletPurpose);
        Task Resolve() => resolution switch
        {
            "apply" => supervisor.Service.ConfirmAndApplyAsync("CLOSED-RETRY", "BANK", true),
            "cancel" => supervisor.Service.CancelUnfundedAsync("CLOSED-RETRY", "No fondos", true),
            _ => supervisor.Service.ResolveByExternalRefundAsync("CLOSED-RETRY", "OUT", "Devuelto", true)
        };
        await Resolve();
        await new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 0);
        await Resolve();
        await using var verify = Db();
        Assert.Equal(resolution == "apply" ? 110m : 100m, (await verify.Giftcards.SingleAsync()).Balance);
        Assert.Equal(resolution == "cancel" ? 0 : 1, await verify.IncomingTransferReceipts.CountAsync());
        Assert.Empty(await verify.ShiftMovements.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_MissingHistoricalReceiptDoesNotInventASecondApplication(bool customer)
    {
        if (customer) await SeedDebtAsync();
        await Transfers.PrepareAsync("HISTORICAL-GAP", customer ? IncomingTransferService.CustomerPurpose : IncomingTransferService.WalletPurpose,
            customer ? "C" : "G", 10m, "Prueba");
        await Transfers.ConfirmAndApplyAsync("HISTORICAL-GAP", "BANK", true);
        // Synthetic legacy inconsistency. Production migration triggers prohibit these deletes.
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM incoming_transfer_receipts;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM transaction_journal WHERE event_type = 'INCOMING_TRANSFER_APPLIED';");
        }
        var supervisor = await TransferSupervisorAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            supervisor.Service.ConfirmAndApplyAsync("HISTORICAL-GAP", "BANK", true));
        Assert.Contains("movimiento previo", error.Message);
        Assert.Single((await supervisor.Service.GetPendingPageAsync()).Items);
        await using var verify = Db();
        Assert.Equal(customer ? 90m : 0m, (await verify.Customers.SingleAsync()).Debt);
        Assert.Equal(customer ? 100m : 110m, (await verify.Giftcards.SingleAsync()).Balance);
        Assert.Empty(await verify.IncomingTransferReceipts.ToListAsync());
        Assert.False(await verify.TransactionJournal.AnyAsync(j => j.EventType == "INCOMING_TRANSFER_APPLIED"));
    }

    [Fact]
    public async Task TransferSupervision_MisreportedHistoricalReceiptCannotHidePendingMoneyOrCloseShift()
    {
        await Transfers.PrepareAsync("WRONG-RECEIPT", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        await Transfers.ConfirmAndApplyAsync("WRONG-RECEIPT", "BANK", true);
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("UPDATE incoming_transfer_receipts SET Amount = 999;");
        var supervisor = await TransferSupervisorAsync();
        Assert.Single((await supervisor.Service.GetPendingPageAsync()).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Service.ConfirmAndApplyAsync("WRONG-RECEIPT", "BANK", true));
        Assert.Contains("comprobante previo", error.Message);
        await using var verify = Db();
        Assert.Equal(110m, (await verify.Giftcards.SingleAsync()).Balance);
        Assert.Single(await verify.AccountEntries.ToListAsync());
    }

    [Fact]
    public async Task TransferSupervision_OldCancellationCannotHideContradictoryBankEvidence()
    {
        await Transfers.PrepareAsync("CONTRADICTORY-CANCEL", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        await Transfers.CancelUnfundedAsync("CONTRADICTORY-CANCEL", "Sin fondos", true);
        await using (var db = Db())
        {
            db.TenderConfirmations.Add(new()
            {
                OperationId = "CONTRADICTORY-CANCEL",
                Amount = 10m,
                Kind = TenderKind.Transfer,
                Purpose = IncomingTransferService.WalletPurpose,
                ExternalReference = "BANK",
                CashierUserId = CommercialTestSession.UserId.ToString(),
                ShiftId = "SHIFT-1",
                RegisterId = "TEST-REGISTER"
            });
            await db.SaveChangesAsync();
        }
        Assert.Single((await Transfers.GetPendingPageAsync()).Items);
        var supervisor = await TransferSupervisorAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Service.CancelUnfundedAsync("CONTRADICTORY-CANCEL", "Sin fondos", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Service.ConfirmAndApplyAsync("CONTRADICTORY-CANCEL", "BANK", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 0));
        await using var verify = Db();
        Assert.Equal(100m, (await verify.Giftcards.SingleAsync()).Balance); Assert.Empty(await verify.AccountEntries.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferSupervision_SessionChangePreservesProofButRollsBackUnapprovedApplication(bool afterProof)
    {
        await Transfers.PrepareAsync("SESSION-TRANSFER", IncomingTransferService.WalletPurpose, "G", 10m, "Prueba");
        var engine = new UserRolePermissionEngine(); var session = new SessionManager();
        Ticketfy.Core.Models.UserModel replacement;
        Guid initial = Guid.NewGuid(), next = Guid.NewGuid();
        await using (var db = Db())
        {
            CommercialTestSession.Create(db, false, userId: initial);
            CommercialTestSession.Create(db, false, userId: next);
            session.StartSession(engine.MapToModel(await db.Users.SingleAsync(u => u.Id == initial)));
            replacement = engine.MapToModel(await db.Users.SingleAsync(u => u.Id == next));
        }
        bool switched = false;
        void Switch()
        {
            if (switched) return;
            switched = true; session.StartSession(replacement);
        }
        IInterceptor interception = afterProof ? new TransferCommitBoundary(Switch) : new TransferPostingBoundary(Switch);
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(interception).Options;
        var service = new IncomingTransferService(new TestDbContextFactory(options), new CommercialOperationContext(session, engine, "TEST-REGISTER"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmAndApplyAsync("SESSION-TRANSFER", "BANK", true));
        Assert.True(switched);
        await using (var db = Db())
        {
            Assert.Single(await db.TenderConfirmations.ToListAsync()); Assert.Empty(await db.IncomingTransferReceipts.ToListAsync());
            Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance); Assert.Empty(await db.AccountEntries.ToListAsync());
            Assert.False(await db.CommandReceipts.AnyAsync(r => r.OperationId == "SESSION-TRANSFER"));
            Assert.Equal(initial.ToString(), (await db.TransactionJournal.SingleAsync(j => j.EventType == "TRANSFER_VERIFIED_MANUALLY")).CashierUserId);
        }
        await service.ConfirmAndApplyAsync("SESSION-TRANSFER", "BANK", true);
        await using var verify = Db();
        Assert.Equal(110m, (await verify.Giftcards.SingleAsync()).Balance);
        Assert.Equal(next.ToString(), (await verify.TransactionJournal.SingleAsync(j => j.EventType == "INCOMING_TRANSFER_APPLIED")).CashierUserId);
    }

    private sealed class TransferCommitBoundary(Action changed) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { changed(); return Task.CompletedTask; }
    }
    private sealed class TransferPostingBoundary(Action changed) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"giftcards\"", StringComparison.Ordinal)) changed();
            return ValueTask.FromResult(result);
        }
    }
}
