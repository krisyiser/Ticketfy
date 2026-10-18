using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Settings;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private HistoricalReconciliationService Historical => new(new TestDbContextFactory(_options), _actor);
    private IncomingTransferService Transfers => new(new TestDbContextFactory(_options), _actor);
    private CommercialReconciliationService Reconciliation => new(new TestDbContextFactory(_options), _actor);
    private async Task SeedHistoricalPurchaseAsync(string id = "OLD-PURCHASE")
    {
        await using var db = Db();
        db.Purchases.Add(new PurchaseEntity { Id = id, SupplierId = "S", SupplierName = "Proveedor", InvoiceNumber = id, TotalCost = 100m, LedgerVersion = 0 });
        await db.SaveChangesAsync();
    }
    private async Task SeedDebtAsync(decimal amount = 100m)
    {
        await using var db = Db(); CommercialTestSession.OpeningDebt(db, "C", amount);
    }
    [Fact]
    public async Task HistoricalSupplierBalance_IsEvidenceBasedIdempotentAndCanBePaidWithoutReceivingStockAgain()
    {
        await SeedHistoricalPurchaseAsync();
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        Assert.True((await purchases.GetStatementAsync("S")).HasUnreconciledHistory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => purchases.PayAsync("OLD-PURCHASE", 1m, "Efectivo", null, "NO-REVIEW"));
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.SupplierPurchase, "OLD-PURCHASE");
        Assert.Equal(0m, preview.LedgerBalance); Assert.Equal(100m, preview.MaximumBalance);
        var result = await Historical.ConfirmAsync(preview.Scope, preview.TargetId, 70m, preview.Fingerprint, "ACTA-01", "Saldo confirmado con proveedor", true, "REVIEW-P");
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, 70m, preview.Fingerprint, "ACTA-01", "Saldo confirmado con proveedor", true, "REVIEW-P");
        Assert.Equal(70m, result.Adjustment);
        Assert.False((await purchases.GetStatementAsync("S")).HasUnreconciledHistory);
        await purchases.PayAsync("OLD-PURCHASE", 20m, "Efectivo", null, "PAY-OLD");
        await purchases.PayAsync("OLD-PURCHASE", 20m, "Efectivo", null, "PAY-OLD");
        Assert.Equal(50m, (await purchases.GetStatementAsync("S")).Balance);
        await using var db = Db();
        Assert.Equal(100m, (await db.Purchases.SingleAsync()).TotalCost);
        Assert.Equal(0, (await db.Purchases.SingleAsync()).LedgerVersion);
        Assert.Empty(await db.InventoryMovements.ToListAsync()); Assert.Single(await db.ShiftMovements.ToListAsync());
        Assert.Single(await Historical.GetHistoryAsync(preview.Scope, preview.TargetId));
        await SeedHistoricalPurchaseAsync("ANOTHER-OLD");
        Assert.True((await purchases.GetStatementAsync("S")).HasUnreconciledHistory);
    }
    [Fact]
    public async Task HistoricalTargets_IncludeInactiveAccountsAndNeverMarkModernPurchasesAsLegacy()
    {
        await SeedHistoricalPurchaseAsync();
        await using var db = Db(); (await db.Customers.SingleAsync()).IsActive = false;
        db.Purchases.Add(new PurchaseEntity { Id = "MODERN", SupplierId = "S", TotalCost = 100m, LedgerVersion = 1 });
        await db.SaveChangesAsync();
        var customer = Assert.Single(await Historical.GetTargetsAsync(HistoricalBalanceScope.CustomerAccount));
        Assert.Equal("C", customer.Id); Assert.Contains("inactivo", customer.Label);
        Assert.Equal("OLD-PURCHASE", Assert.Single(await Historical.GetTargetsAsync(HistoricalBalanceScope.SupplierPurchase)).Id);
    }
    [Fact]
    public async Task HistoricalZeroReview_InvalidatesPreviewAndLaterCorrectionPreservesPayments()
    {
        await SeedHistoricalPurchaseAsync();
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.SupplierPurchase, "OLD-PURCHASE");
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, 0m, preview.Fingerprint, "ACT-0", "Sin deuda comprobada", true, "REVIEW-0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Historical.ConfirmAsync(preview.Scope, preview.TargetId, 60m,
            preview.Fingerprint, "ACT-1", "Corrección", true, "STALE-ZERO"));
        await using var db = Db(); Assert.Empty(await db.AccountEntries.ToListAsync());
        preview = await Historical.PreviewAsync(preview.Scope, preview.TargetId);
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, 60m, preview.Fingerprint, "ACT-1", "Nuevo estado comprobado", true, "REVIEW-1");
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        await purchases.PayAsync("OLD-PURCHASE", 20m, "Efectivo", null, "HIST-PAY");
        preview = await Historical.PreviewAsync(preview.Scope, preview.TargetId); Assert.Equal(40m, preview.LedgerBalance);
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, 30m, preview.Fingerprint, "ACT-2", "Saldo actual comprobado", true, "REVIEW-2");
        Assert.Equal(30m, (await purchases.GetStatementAsync("S")).Balance);
        Assert.Equal(20m, (await db.SupplierPayments.SingleAsync()).Amount);
        Assert.Equal(20m, (await db.ShiftMovements.SingleAsync()).Amount);
        Assert.Equal(3, await db.HistoricalBalanceReconciliations.CountAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }
    [Fact]
    public async Task HistoricalCustomerAdjustment_PreservesOriginalEntriesAndCashAndRejectsStalePreview()
    {
        await SeedDebtAsync();
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.CustomerAccount, "C");
        await CustomersService().RegisterCustomerPaymentAsync("C", 10m, "Efectivo", "Abono", "PAY-BEFORE");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Historical.ConfirmAsync(preview.Scope, "C", 80m, preview.Fingerprint, "ACTA", "Revisado", true, "STALE"));
        preview = await Historical.PreviewAsync(preview.Scope, "C");
        await Historical.ConfirmAsync(preview.Scope, "C", 80m, preview.Fingerprint, "ACTA", "Corrección documentada", true, "REVIEW-C");
        await Historical.ConfirmAsync(preview.Scope, "C", 80m, preview.Fingerprint, "ACTA", "Corrección documentada", true, "REVIEW-C");
        Assert.Equal(80m, (await CustomersService().GetByIdAsync("C"))!.Debt);
        Assert.False((await CustomersService().GetStatementAsync("C")).HasUnreconciledHistory);
        await using var db = Db();
        Assert.Equal(100m, (await db.AccountEntries.SingleAsync(e => e.EntryType == "MIGRATION_OPENING")).Amount);
        Assert.Equal(-10m, (await db.AccountEntries.SingleAsync(e => e.EntryType == "HISTORICAL_BALANCE_ADJUSTMENT")).Amount);
        Assert.Equal(10m, (await db.ShiftMovements.SingleAsync()).Amount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Historical.ConfirmAsync(preview.Scope, "C", 79m, preview.Fingerprint, "ACTA", "Corrección documentada", true, "REVIEW-C"));
    }
    [Fact]
    public async Task HistoricalReview_RequiresExplicitPermissionEvidenceAndValidBalance()
    {
        await SeedHistoricalPurchaseAsync();
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.SupplierPurchase, "OLD-PURCHASE");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Historical.ConfirmAsync(preview.Scope, preview.TargetId, 0, preview.Fingerprint, "", "", false, "INVALID"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Historical.ConfirmAsync(preview.Scope, preview.TargetId, 101m, preview.Fingerprint, "ACTA", "Revisión", true, "EXCESS"));
        await using var db = Db();
        var manager = CommercialTestSession.Create(db, false, "MANAGER", Guid.NewGuid(), "GERENTE");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new HistoricalReconciliationService(new TestDbContextFactory(_options), manager).PreviewAsync(preview.Scope, preview.TargetId));
        var c = await db.Customers.SingleAsync(); c.Debt = 25m; await db.SaveChangesAsync();
        Assert.True((await CustomersService().GetStatementAsync("C")).HasUnreconciledHistory);
        var mismatch = await Historical.PreviewAsync(HistoricalBalanceScope.CustomerAccount, "C");
        await Historical.ConfirmAsync(mismatch.Scope, "C", 10m, mismatch.Fingerprint, "ACTA-C", "Verificación de saldo sin ledger", true, "REPAIR-CACHE");
        Assert.Equal(10m, (await CustomersService().GetStatementAsync("C")).Balance);
        Assert.Equal(10m, (await CustomersService().GetByIdAsync("C"))!.Debt);
    }
    [Fact]
    public async Task HistoricalAdjustment_FailureRollsBackProjectionLedgerAndAudit()
    {
        await SeedDebtAsync();
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.CustomerAccount, "C");
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_reconcile BEFORE INSERT ON historical_balance_reconciliations BEGIN SELECT RAISE(ABORT, 'injected disk error'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => Historical.ConfirmAsync(preview.Scope, "C", 75m, preview.Fingerprint, "ACTA", "Revisión", true, "FAIL-HIST"));
        Assert.Equal(100m, (await CustomersService().GetByIdAsync("C"))!.Debt);
        Assert.Single(await db.AccountEntries.ToListAsync()); Assert.Empty(await db.HistoricalBalanceReconciliations.ToListAsync());
        Assert.False(await db.CommandReceipts.AnyAsync(r => r.OperationId == "FAIL-HIST"));
    }
    [Fact]
    public async Task CustomerTransfer_IsRecoverableAndNeverEntersCashOrCreatesSales()
    {
        await SeedDebtAsync();
        await Transfers.PrepareAsync("BANK-C", IncomingTransferService.CustomerPurpose, "C", 30m, "Abono SPEI");
        await Transfers.PrepareAsync("BANK-C", IncomingTransferService.CustomerPurpose, "C", 30m, "Abono SPEI");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ConfirmAndApplyAsync("BANK-C", "SPEI-001", false));
        await Reconciliation.ConfirmTransferAsync("BANK-C", 30m, "SPEI-001", IncomingTransferService.CustomerPurpose);
        Assert.Equal("SPEI-001", Assert.Single(await Transfers.GetPendingAsync()).Reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 0));
        var restarted = new IncomingTransferService(new TestDbContextFactory(_options), _actor);
        await restarted.ConfirmAndApplyAsync("BANK-C", "SPEI-001", true);
        await restarted.ConfirmAndApplyAsync("BANK-C", "SPEI-001", true);
        Assert.Equal(70m, (await CustomersService().GetByIdAsync("C"))!.Debt);
        Assert.Empty(await Transfers.GetPendingAsync());
        await using var db = Db();
        Assert.Single(await db.CustomerPayments.ToListAsync()); Assert.Single(await Transfers.GetReceiptsAsync());
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.ShiftMovements.ToListAsync());
        await new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 0);
        Assert.True(Assert.Single(await Reconciliation.ReconcileAsync(DateTime.Today)).IsBalanced);
    }
    [Fact]
    public async Task WalletTransfer_AppliesOnceAndCannotUseSaleProofOrWrongDestination()
    {
        await Transfers.PrepareAsync("BANK-W", IncomingTransferService.WalletPurpose, "G", 25m, "Recarga");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.PrepareAsync("BANK-W", IncomingTransferService.WalletPurpose, "G", 26m, "Recarga"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.ConfirmTransferAsync("BANK-W", 25m, "BANK-W-REF"));
        await Transfers.ConfirmAndApplyAsync("BANK-W", "BANK-W-REF", true);
        await Transfers.ConfirmAndApplyAsync("BANK-W", "BANK-W-REF", true);
        await using var db = Db();
        Assert.Equal(125m, (await db.Giftcards.SingleAsync()).Balance);
        Assert.Equal(25m, (await db.AccountEntries.SingleAsync(e => e.EntryType == "TRANSFER_LOAD")).Amount);
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        await Reconciliation.ConfirmTransferAsync("SALE-PROOF", 20m, "SPEI-SALE");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GiftcardService(new TestDbContextFactory(_options), _actor)
            .RechargeAsync("G", 20m, PaymentMethod.Transferencia, "SHIFT-1", "SALE-PROOF"));
    }
    [Fact]
    public async Task IncomingReference_CannotFundBothASaleAndAnAccountEvenWithDifferentCase()
    {
        await Reconciliation.ConfirmTransferAsync("SALE-T", 20m, "Bank-Global");
        await Sales.SaveAsync(Sale("SALE-T") with { CashAmount = 0m, PaymentMethod = "Transferencia SPEI" });
        await Transfers.PrepareAsync("LOAD-T", IncomingTransferService.WalletPurpose, "G", 20m, "Recarga");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ConfirmAndApplyAsync("LOAD-T", "bank-global", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(Sale("LOAD-T")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(Sale("LOAD-T")));
        await using var db = Db(); Assert.Single(await db.IncomingTransferReceipts.ToListAsync());
        Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance);
    }
    [Fact]
    public async Task CancelUnfundedTransfer_IsAuditedAndCannotBeConfirmedOrCancelFundedDeposit()
    {
        await Transfers.PrepareAsync("UNFUNDED", IncomingTransferService.WalletPurpose, "G", 10m, "Recarga");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.CancelUnfundedAsync("UNFUNDED", "Sin depósito", false));
        await Transfers.CancelUnfundedAsync("UNFUNDED", "Sin depósito", true);
        await Transfers.CancelUnfundedAsync("UNFUNDED", "Sin depósito", true);
        Assert.Empty(await Transfers.GetPendingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ConfirmAndApplyAsync("UNFUNDED", "NO-DEPOSIT", true));
        await Transfers.PrepareAsync("FUNDED", IncomingTransferService.WalletPurpose, "G", 10m, "Recarga");
        await Reconciliation.ConfirmTransferAsync("FUNDED", 10m, "DEPOSIT", IncomingTransferService.WalletPurpose);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.CancelUnfundedAsync("FUNDED", "No permitido", true));
    }
    [Fact]
    public async Task FundedButUnapplicableTransfer_RequiresVerifiedBankRefundAndNeverCreatesCash()
    {
        await SeedDebtAsync();
        await Transfers.PrepareAsync("UNAPPLIED", IncomingTransferService.CustomerPurpose, "C", 100m, "Abono");
        await Reconciliation.ConfirmTransferAsync("UNAPPLIED", 100m, "DEPOSIT-100", IncomingTransferService.CustomerPurpose);
        await CustomersService().RegisterCustomerPaymentAsync("C", 100m, "Efectivo", "Pagó por otro medio", "CASH-100");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ConfirmAndApplyAsync("UNAPPLIED", "DEPOSIT-100", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ResolveByExternalRefundAsync("UNAPPLIED", "REFUND-100", "Duplicó depósito", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ResolveByExternalRefundAsync("UNAPPLIED", "deposit-100", "Referencia incorrecta", true));
        await Transfers.ResolveByExternalRefundAsync("UNAPPLIED", "REFUND-100", "Duplicó depósito", true);
        await Transfers.ResolveByExternalRefundAsync("UNAPPLIED", "REFUND-100", "Duplicó depósito", true);
        Assert.Equal(TransferOutcome.RefundedExternally, Assert.Single(await Transfers.GetReceiptsAsync()).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ConfirmAndApplyAsync("UNAPPLIED", "DEPOSIT-100", true));
        await using var db = Db(); Assert.Single(await db.CustomerPayments.ToListAsync()); Assert.Single(await db.ShiftMovements.ToListAsync());
        Assert.Equal(100m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Empty(await Transfers.GetPendingAsync());
    }
    [Fact]
    public async Task ExternalRefundReference_CannotSettleBothAnUnappliedDepositAndASaleReturn()
    {
        await Transfers.PrepareAsync("REFUND-W", IncomingTransferService.WalletPurpose, "G", 10m, "Recarga");
        await Reconciliation.ConfirmTransferAsync("REFUND-W", 10m, "BANK-IN-1", IncomingTransferService.WalletPurpose);
        await Transfers.ResolveByExternalRefundAsync("REFUND-W", "Bank-Out-1", "Depósito devuelto", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GiftcardService(new TestDbContextFactory(_options), _actor)
            .RechargeAsync("G", 10m, PaymentMethod.Transferencia, "SHIFT-1", "REFUND-W"));
        await Reconciliation.ConfirmTransferAsync("REFUND-S", 20m, "BANK-IN-2");
        var sale = await Sales.SaveAsync(Sale("REFUND-S") with { CashAmount = 0m, PaymentMethod = "Transferencia SPEI" });
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Devolución", operationId: "RETURN-S");
        var pending = Assert.Single(await Reconciliation.GetPendingRefundsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciliation.SettleRefundAsync(pending.Id, "bank-out-1", "DUP-REFUND"));
        await Reconciliation.SettleRefundAsync(pending.Id, "Bank-Out-2", "SETTLE-2");
        await Reconciliation.SettleRefundAsync(pending.Id, "bank-out-2", "SETTLE-2");
        await Transfers.PrepareAsync("REFUND-W2", IncomingTransferService.WalletPurpose, "G", 10m, "Recarga");
        await Reconciliation.ConfirmTransferAsync("REFUND-W2", 10m, "BANK-IN-3", IncomingTransferService.WalletPurpose);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.ResolveByExternalRefundAsync("REFUND-W2", "bank-out-2", "Duplicado", true));
        await using var db = Db(); Assert.Single(await db.RefundSettlements.ToListAsync());
        Assert.Equal(100m, (await db.Giftcards.SingleAsync()).Balance); Assert.Empty(await db.ShiftMovements.ToListAsync());
    }
    [Fact]
    public async Task TransferApplicationFailure_RollsBackBalanceButKeepsDepositConfirmationForRecovery()
    {
        await SeedDebtAsync();
        await Transfers.PrepareAsync("POWER-T", IncomingTransferService.CustomerPurpose, "C", 40m, "Abono");
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_transfer BEFORE INSERT ON incoming_transfer_receipts BEGIN SELECT RAISE(ABORT, 'injected disk full'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => Transfers.ConfirmAndApplyAsync("POWER-T", "BANK-POWER", true));
        Assert.Equal(100m, (await CustomersService().GetByIdAsync("C"))!.Debt);
        Assert.Empty(await db.CustomerPayments.ToListAsync()); Assert.Single(await db.TenderConfirmations.ToListAsync());
        Assert.Single(await Transfers.GetPendingAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_transfer;");
        await Transfers.ConfirmAndApplyAsync("POWER-T", "BANK-POWER", true);
        Assert.Equal(60m, (await CustomersService().GetByIdAsync("C"))!.Debt);
    }
    [Fact]
    public async Task IncomingTransfers_RejectOtherRegisterAndUnauthorizedCashier()
    {
        await Transfers.PrepareAsync("OWNED", IncomingTransferService.WalletPurpose, "G", 10m, "Recarga");
        await using var db = Db();
        var other = CommercialTestSession.Create(db, true, "OTHER", Guid.NewGuid());
        var otherTransfers = new IncomingTransferService(new TestDbContextFactory(_options), other);
        Assert.Empty(await otherTransfers.GetPendingAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => otherTransfers.ConfirmAndApplyAsync("OWNED", "REF", true));
        var cashier = CommercialTestSession.Create(db, true, "CASHIER-T", Guid.NewGuid(), "CAJERO");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new IncomingTransferService(new TestDbContextFactory(_options), cashier)
            .PrepareAsync("FORBIDDEN", IncomingTransferService.WalletPurpose, "G", 1m, ""));
    }
    [AvaloniaFact]
    public async Task Ui_RoleMatrix_ContainsEveryBackendPermissionAndKeepsHistoricalReviewRestricted()
    {
        var vm = new UsuariosSettingsViewModel();
        await vm.LoadRolePermissionsAsync("ADMINISTRADOR");
        Assert.Equal(PermissionCatalog.All.Order(), vm.RolePermissions.Select(p => p.Key).Order());
        Assert.All(vm.RolePermissions, p => Assert.True(p.IsGranted));
        await vm.LoadRolePermissionsAsync("GERENTE");
        Assert.False(vm.RolePermissions.Single(p => p.Key == PermissionCatalog.HistoryReconcile).IsGranted);
        Assert.True(vm.RolePermissions.Single(p => p.Key == PermissionCatalog.PaymentsReconcile).IsGranted);
    }
    [AvaloniaFact]
    public async Task Ui_HistoricalAndTransferCommands_PreserveEvidenceAndRejectDoubleClicks()
    {
        await SeedDebtAsync(); var factory = new TestDbContextFactory(_options);
        await using var db = Db();
        var vm = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor), new PurchaseService(factory, _actor),
            new ShiftService(factory, _actor), new GiftcardService(factory, _actor), Reconciliation, factory, _actor, Sales);
        var window = Show(new CommercialControlView(), vm);
        try
        {
            await vm.RefreshAsync(); Assert.True(vm.CanHistorical); Assert.True(vm.CanTransfers);
            vm.Customer = await CustomersService().GetByIdAsync("C");
            vm.HistoricalCustomer = vm.HistoricalCustomers.Single(c => c.Id == "C");
            await vm.PreviewHistoricalCommand.ExecuteAsync(null);
            vm.HistoricalSupplier = true; vm.HistoricalSupplier = false;
            vm.HistoricalEvidenceVerified = true;
            await vm.ConfirmHistoricalCommand.ExecuteAsync(null);
            Assert.Empty(await db.HistoricalBalanceReconciliations.ToListAsync());
            await vm.PreviewHistoricalCommand.ExecuteAsync(null);
            vm.ReconciledBalance = 80m; vm.Reason = "Acta de prueba"; vm.Reference = "ACTA-UI"; vm.HistoricalEvidenceVerified = true;
            await vm.ConfirmHistoricalCommand.ExecuteAsync(null); await vm.ConfirmHistoricalCommand.ExecuteAsync(null);
            Assert.Single(await db.HistoricalBalanceReconciliations.ToListAsync());
            vm.Amount = 20m;
            await vm.PrepareTransferCommand.ExecuteAsync(null); Assert.Equal(0m, vm.Amount); Assert.NotNull(vm.PendingTransfer);
            vm.TransferReference = "UI-TRANSFER"; vm.TransferFundsVerified = true;
            await vm.ApplyTransferCommand.ExecuteAsync(null); await vm.ApplyTransferCommand.ExecuteAsync(null);
            Assert.Equal(60m, (await CustomersService().GetByIdAsync("C"))!.Debt);
            Assert.Single(await db.IncomingTransferReceipts.ToListAsync());
            await vm.TransferHistoryCommand.ExecuteAsync(null); Assert.Single(vm.Rows);
            Assert.Contains("UI-TRANSFER", vm.Rows.Single().Event);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task Migration_BackfillsConsumedTransferAndProtectsNewEvidenceFromSqlChanges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.GetService<IMigrator>().MigrateAsync("20261018213827_CommercialLedgersAndRegisterOwnership");
        var actor = CommercialTestSession.Create(db);
        // Seed a prior-version posted transfer without invoking services that require the new tables.
        db.Sales.Add(new SaleEntity
        {
            Id = "BEFORE-UPGRADE",
            OperationId = "OLD-TRANSFER",
            Total = 20m,
            LedgerVersion = 1,
            RegisterId = "TEST-REGISTER",
            ShiftId = "SHIFT-1",
            CashierUserId = CommercialTestSession.UserId.ToString()
        });
        db.SalePayments.Add(new SalePaymentEntity
        {
            SaleId = "BEFORE-UPGRADE",
            OperationId = "OLD-TRANSFER",
            Amount = 20m,
            Kind = TenderKind.Transfer,
            ExternalReference = "OLD-BANK-REF",
            ShiftId = "SHIFT-1",
            RegisterId = "TEST-REGISTER",
            CashierUserId = CommercialTestSession.UserId.ToString()
        });
        db.Purchases.Add(new PurchaseEntity { Id = "OLD-P", SupplierId = "OLD-S", TotalCost = 50m });
        await db.SaveChangesAsync(); await db.Database.MigrateAsync();
        Assert.Equal("OLD-BANK-REF", (await db.IncomingTransferReceipts.SingleAsync()).ExternalReference);
        var historical = new HistoricalReconciliationService(new TestDbContextFactory(options), actor);
        var preview = await historical.PreviewAsync(HistoricalBalanceScope.SupplierPurchase, "OLD-P");
        await historical.ConfirmAsync(preview.Scope, "OLD-P", 50m, preview.Fingerprint, "UPGRADE-ACT", "Conciliación", true, "UPGRADE-REVIEW");
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM incoming_transfer_receipts;"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE historical_balance_reconciliations SET ConfirmedBalance=0;"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Purchases SET TotalCost=0 WHERE Id='OLD-P';"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE sale_payments SET Amount=0;"));
    }
}
