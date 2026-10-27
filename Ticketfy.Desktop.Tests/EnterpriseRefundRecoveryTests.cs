using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CreditRefund_PreparedBeforeFundsReservesBalanceBlocksCloseAndSurvivesRecreatedService()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Devolver saldo", "DURABLE");
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Devolver saldo", "DURABLE");
        Assert.Single(await Credits.GetPendingRefundsAsync());
        Assert.Empty(await Credits.GetDocumentsAsync(AccountKind.Customer, "C"));
        await using var db = Db();
        Assert.Equal(-10m, (await db.Customers.SingleAsync()).Debt);
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 1m, "Efectivo", "repetir", "DUPLICATE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 20m));
        var recreated = new AccountCreditSettlementService(new TestDbContextFactory(_options), _actor);
        await recreated.ResolveRefundAsync("DURABLE", true, null, "Se comprobó entrega única", true);
        await recreated.ResolveRefundAsync("DURABLE", true, null, "Se comprobó entrega única", true);
        Assert.Empty(await recreated.GetPendingRefundsAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(10m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(0m, (await db.Customers.SingleAsync()).Debt);
        await new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 10m);
    }

    [Fact]
    public async Task CreditRefund_FailedCommitRetainsReservationAndCanBeResolvedByAnotherManager()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Efectivo", "Devolver", "INTERRUPTED");
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_fail_resolution BEFORE INSERT ON credit_refund_resolutions BEGIN SELECT RAISE(ABORT, 'simulated full disk'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => Credits.ResolveRefundAsync("INTERRUPTED", true, null, "Entregado", true));
        Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Single(await Credits.GetPendingRefundsAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_fail_resolution;");
        var original = await db.Users.SingleAsync(); original.IsActive = false;
        await db.SaveChangesAsync();
        var supervisorId = Guid.NewGuid();
        var supervisor = CommercialTestSession.Create(db, false, userId: supervisorId, role: "GERENTE");
        var recovery = new AccountCreditSettlementService(new TestDbContextFactory(_options), supervisor);
        await recovery.ResolveRefundAsync("INTERRUPTED", true, null, "Evidencia revisada por gerente", true);
        await recovery.ResolveRefundAsync("INTERRUPTED", true, null, "Evidencia revisada por gerente", true);
        Assert.Equal(CommercialTestSession.UserId.ToString(), (await db.AccountCreditSettlements.SingleAsync()).CashierUserId);
        Assert.Equal(supervisorId.ToString(), (await db.CreditRefundResolutions.SingleAsync()).ApproverUserId);
        var cash = (await db.ShiftMovements.ToListAsync()).Single(m => m.OperationId == "INTERRUPTED");
        Assert.Equal(CommercialTestSession.UserId.ToString(), cash.CashierUserId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ResolveRefundAsync("INTERRUPTED", false, null, "Cambiar decisión", true));
        var settled = await db.CreditRefundResolutions.SingleAsync();
        settled.Reason = "tamper";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CreditRefund_NotExecutedReleasesReservationWithoutMoneyAndRequiresCorrectRegister()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", source, 10m, "Transferencia", "Preparar", "VOID");
        await using var db = Db();
        var other = CommercialTestSession.Create(db, false, "OTHER", Guid.NewGuid());
        var foreign = new AccountCreditSettlementService(new TestDbContextFactory(_options), other);
        Assert.Empty(await foreign.GetPendingRefundsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.ResolveRefundAsync("VOID", false, null, "sin fondos", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("VOID", false, null, "sin fondos", false));
        await Credits.ResolveRefundAsync("VOID", false, null, "Banco comprobado sin envío", true);
        await Credits.ResolveRefundAsync("VOID", false, null, "Banco comprobado sin envío", true);
        Assert.Equal(-10m, Assert.Single(await Credits.GetDocumentsAsync(AccountKind.Customer, "C")).Balance);
        Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Theory]
    [InlineData("Tarjeta")]
    [InlineData("Transferencia")]
    public async Task SupplierCredit_BankRefundsDoNotInventCashAndRepeatSafely(string method)
    {
        await new ShiftService(new TestDbContextFactory(_options), _actor).RegisterMovementAsync(100m, false, "Fondo", "FUND");
        var source = await PaidSupplierReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Supplier, "S", source.Id, 8m, method, "Solicitar devolución", "BANK-S");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ResolveRefundAsync("BANK-S", true, null, "Recibido", true));
        await Credits.ResolveRefundAsync("BANK-S", true, "receipt-s", "Recibido y verificado", true);
        await Credits.ResolveRefundAsync("BANK-S", true, "RECEIPT-S", "Recibido y verificado", true);
        Assert.Empty(await Credits.GetDocumentsAsync(AccountKind.Supplier, "S"));
        await using var db = Db();
        Assert.Equal(88m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal("RECEIPT-S", (await db.AccountCreditSettlements.SingleAsync()).ExternalReference);
        await new ShiftService(new TestDbContextFactory(_options), _actor).CloseAsync("SHIFT-1", 88m);
    }
}
