using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task SameKeyDifferentPayload_IsRejectedEvenWhenTotalIsUnchanged()
    {
        var sale = Sale("BOUND");
        await Sales.SaveAsync(sale);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale with { CustomerId = "C" }));
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        await shifts.RegisterMovementAsync(5m, true, "Retiro", "CASH-KEY");
        await Assert.ThrowsAsync<InvalidOperationException>(() => shifts.RegisterMovementAsync(5m, false, "Retiro", "CASH-KEY"));
        await using var db = Db();
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(15m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Fact]
    public async Task CustomerLedger_IncludesOnlyCreditAndPaymentsNotCashPurchases()
    {
        await Sales.SaveAsync(Sale("CASH-CUSTOMER") with { CustomerId = "C" });
        await Sales.SaveAsync(Sale("CREDIT-CUSTOMER") with { CashAmount = 0, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        var customers = new CustomerService(new TestDbContextFactory(_options), _actor);
        Assert.Equal(20m, (await customers.GetStatementAsync("C")).Balance);
        await customers.RegisterCustomerPaymentAsync("C", 10m, "Efectivo", "Abono", "PAY");
        await customers.RegisterCustomerPaymentAsync("C", 10m, "Efectivo", "Abono", "PAY");
        Assert.Equal(10m, (await customers.GetStatementAsync("C")).Balance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.RegisterCustomerPaymentAsync("C", 11m, "Efectivo", "Exceso", "PAY-EXCESS"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.UpdateDebtAsync("C", 0m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.UpdatePointsAsync("C", 1000));
    }

    [Fact]
    public async Task Promotion_IsAppliedAndRevalidatedByBackend()
    {
        var promotions = new PromotionService(new TestDbContextFactory(_options), _actor);
        await promotions.SaveAsync(new PromotionDto("PROMO", "Diez por ciento", 10m, true, PromotionType.PercentageDiscount, "P",
            StartDate: DateTime.Today.AddDays(-1), EndDate: DateTime.Today.AddDays(1)));
        var item = new CartItemDto("P", "Producto", 10m, 2) { OriginalUnitPrice = 10m };
        var priced = (await promotions.EvaluateAndApplyPromotionsAsync([item])).Single();
        Assert.Equal(2m, priced.AppliedDiscountAmount);
        var dto = Sale("PROMOTION") with
        {
            Total = 18m,
            CashAmount = 18m,
            PaidAmount = 18m,
            Items = [new("P", "Producto", 10m, 0m, 2, "Pza", "General", 2m, 18m, 10m, 2m, "PROMO")]
        };
        await Sales.SaveAsync(dto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(dto with
        {
            Id = "FORGED",
            OperationId = "FORGED",
            Items = [dto.Items[0] with { AppliedPromotionId = "NONEXISTENT" }]
        }));
        await promotions.DeleteAsync("PROMO");
        Assert.Equal(0m, (await promotions.EvaluateAndApplyPromotionsAsync([item])).Single().AppliedDiscountAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(dto with { Id = "EXPIRED", OperationId = "EXPIRED" }));
    }

    [Fact]
    public async Task TransferAndExternalRefund_RequireEvidenceAndRemainSeparateFromCash()
    {
        var reconciliation = new CommercialReconciliationService(new TestDbContextFactory(_options), _actor);
        var sale = Sale("TRANSFER") with { CashAmount = 0m, PaymentMethod = "Transferencia SPEI" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale));
        await reconciliation.ConfirmTransferAsync("TRANSFER", 20m, "BANK-TRANSFER-1");
        await reconciliation.ConfirmTransferAsync("TRANSFER", 20m, "BANK-TRANSFER-1");
        var saved = await Sales.SaveAsync(sale);
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Devolución", operationId: "TRANSFER-RETURN");
        var pending = Assert.Single(await reconciliation.GetPendingRefundsAsync());
        Assert.Equal(TenderKind.Transfer, pending.Kind);
        await reconciliation.SettleRefundAsync(pending.Id, "BANK-REFUND-1", "SETTLED");
        await reconciliation.SettleRefundAsync(pending.Id, "BANK-REFUND-1", "SETTLED");
        Assert.Empty(await reconciliation.GetPendingRefundsAsync());
        await using var db = Db();
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        Assert.Single(await db.RefundSettlements.ToListAsync());
    }

    [Fact]
    public async Task CardRefund_DoesNotInventCashOrModifyOriginalPayment()
    {
        await using var db = Db();
        CommercialTestSession.ConfirmCard(db, "CARD", 20m);
        var saved = await Sales.SaveAsync(Sale("CARD") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" });
        await Sales.CancelSaleAsync(saved.Id, "Reembolso");
        Assert.Equal(20m, (await db.SalePayments.SingleAsync()).Amount);
        Assert.Equal(TenderState.RequiresReconciliation, (await db.PaymentRefunds.SingleAsync()).State);
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task LoyaltySaleAndReturn_AreBalancedAndDoNotRewardTwice()
    {
        await using var db = Db();
        (await db.Customers.SingleAsync()).PuntosSaldo = 100;
        await db.SaveChangesAsync();
        var saved = await Sales.SaveAsync(Sale("POINTS") with { CashAmount = 0m, CustomerId = "C", PaymentMethod = "Puntos de Fidelidad" });
        await Sales.CancelSaleAsync(saved.Id, "Devuelto");
        await Sales.CancelSaleAsync(saved.Id, "Devuelto");
        await db.Entry(await db.Customers.SingleAsync()).ReloadAsync();
        Assert.Equal(100d, (await db.Customers.SingleAsync()).PuntosSaldo);
        Assert.Equal(0m, (await db.AccountEntries.ToListAsync()).Sum(e => e.Amount));
        Assert.Empty(await db.ShiftMovements.ToListAsync());
    }

    [Fact]
    public async Task FractionalReturns_NeverCreateExtraCentsOrStock()
    {
        await using var db = Db();
        var product = await db.Products.SingleAsync();
        product.IsBulk = true; product.Cost = 0.01m; product.Price = 0.01m;
        await db.SaveChangesAsync();
        var sale = new SaleDto("SMALL", "", [new("P", "Producto", .01m, .01m, 1.5, "Kg", "General", 0m, .02m)],
            .02m, 0m, 0m, .02m, 0m, "Efectivo", null, false, false, null, CashAmount: .02m, OperationId: "SMALL");
        var saved = await Sales.SaveAsync(sale);
        for (int i = 0; i < 3; i++) await Sales.ProcessPartialReturnAsync(saved.Id, "P", .5, "Fracción", operationId: "FRACTION-" + i);
        Assert.Equal(.02m, (await db.ReturnLines.ToListAsync()).Sum(r => r.CostReversed));
        Assert.Equal(.02m, (await db.PaymentRefunds.ToListAsync()).Sum(r => r.Amount));
        await db.Entry(product).ReloadAsync();
        Assert.Equal(20d, product.Stock);
    }

    [Fact]
    public async Task CloseFailure_RollsBackShiftAndCashupAndRetryClosesOnce()
    {
        await Sales.SaveAsync(Sale("CLOSE-FAULT"));
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_close BEFORE INSERT ON cashups BEGIN SELECT RAISE(ABORT, 'simulated disk failure'); END;");
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        var count = new CashupEntity { Id = "COUNT", OperationId = "CLOSE-FAULT", Type = "Final", ClosedCashAmount = 20m };
        // An operation key cannot be reused between sale and close.
        count.OperationId = "COUNT-FAULT";
        await Assert.ThrowsAsync<DbUpdateException>(() => shifts.CloseWithCashupAsync("SHIFT-1", count));
        Assert.Equal(1, (await db.Shifts.AsNoTracking().SingleAsync()).IsOpen);
        Assert.Empty(await db.Cashups.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_close;");
        await shifts.CloseWithCashupAsync("SHIFT-1", count);
        await shifts.CloseWithCashupAsync("SHIFT-1", count);
        Assert.Single(await db.Cashups.ToListAsync());
    }

    [Fact]
    public async Task CapturedCardBeforeLocalAcknowledgement_IsRecoveredWithoutNewCharge()
    {
        var sale = Sale("POWER") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" };
        await Sales.BeginCheckoutAsync(sale);
        await using var db = Db();
        CommercialTestSession.ConfirmCard(db, "POWER", 20m);
        Assert.Equal(1, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Single(await db.PaymentTerminalOperations.ToListAsync());
    }

    [Fact]
    public async Task CheckoutRetry_IgnoresClientClockButRejectsChangedAmount()
    {
        var dto = Sale("RETRY");
        await Sales.BeginCheckoutAsync(dto);
        await Sales.BeginCheckoutAsync(dto with { Date = "client-clock-changed" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.BeginCheckoutAsync(dto with { CashAmount = 5m }));
    }

    [Fact]
    public async Task UnauthorizedUserCannotOperateOtherCashiersOpenShift()
    {
        await using var db = Db();
        var other = CommercialTestSession.Create(db, false, userId: Guid.NewGuid());
        var sales = new SaleService(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sales.SaveAsync(Sale("WRONG-OWNER")));
        var shifts = new ShiftService(new TestDbContextFactory(_options), other);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => shifts.CloseAsync("SHIFT-1", 0m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shifts.OpenAsync(0m, "SECOND-OPEN"));
    }

    [Fact]
    public async Task WalletCreationRechargeAndHistory_AreCashBacked()
    {
        var wallets = new GiftcardService(new TestDbContextFactory(_options), _actor);
        await wallets.CreateCardAsync("NEW-WALLET", 5m, "C");
        var wallet = (await wallets.GetByCardNumberAsync("NEW-WALLET"))!;
        await wallets.RechargeAsync(wallet.Id, 10m, PaymentMethod.Efectivo, "SHIFT-1", "LOAD");
        await wallets.RechargeAsync(wallet.Id, 10m, PaymentMethod.Efectivo, "SHIFT-1", "LOAD");
        await Assert.ThrowsAsync<InvalidOperationException>(() => wallets.RechargeAsync(wallet.Id, 11m, PaymentMethod.Efectivo, "SHIFT-1", "LOAD"));
        Assert.Equal(15m, (await wallets.GetByCardNumberAsync("NEW-WALLET"))!.Balance);
        await using var db = Db();
        Assert.Equal(15m, (await db.ShiftMovements.ToListAsync()).Sum(m => m.Amount));
        Assert.Equal(15m, (await db.AccountEntries.Where(e => e.AccountId == "NEW-WALLET").ToListAsync()).Sum(e => e.Amount));
    }

    [Fact]
    public async Task AdministrativeChanges_RequireBackendPermissionsAndPreserveAudit()
    {
        await using var db = Db();
        var cashier = CommercialTestSession.Create(db, false, "CASHIER", Guid.NewGuid(), "CAJERO");
        var settings = new SettingsService(new TestDbContextFactory(_options), cashier);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => settings.SetAsync("RolePermissions_CAJERO", "{\"users.manage\":true}"));
        var users = new UserService(db, operations: cashier);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "Intruso", "intruder", "ADMIN", "pw", null));
        var adminSettings = new SettingsService(new TestDbContextFactory(_options), _actor);
        await adminSettings.SetAsync("BusinessName", "Tienda de prueba");
        Assert.Contains(await db.TransactionJournal.ToListAsync(), j => j.EventType == "SETTING_CHANGED" && j.CashierUserId == CommercialTestSession.UserId.ToString());
    }

    [Fact]
    public async Task MigratedDatabase_ProtectsLedgersEvenFromDirectSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        var actor = CommercialTestSession.Create(db);
        db.Products.Add(new ProductEntity { Id = "P", Barcode = "MIG", Name = "Producto", Price = 10m, Cost = 3m, Stock = 20 });
        await db.SaveChangesAsync();
        var service = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), actor);
        await service.SaveAsync(Sale("MIGRATED"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE sale_payments SET Amount=999;"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_movements;"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE sales SET total=1;"));
        Assert.Single(await db.SalePayments.ToListAsync());
    }
}
