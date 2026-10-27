using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task EnterpriseRecovery_ApprovalSurvivesFailedSaleCommitAndStartupFinishesExactlyOnce()
    {
        await BeginCashierRecoveryAsync("AFTER-APPROVAL");
        await using var db = Db();
        var supervisor = SalesFor(CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "SUPERVISOR"));
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_interrupt_checkout BEFORE INSERT ON sales BEGIN SELECT RAISE(ABORT, 'simulated interruption'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => supervisor.ResolveInterruptedCheckoutAsync("AFTER-APPROVAL", true, true, "Fondos comprobados"));
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "CHECKOUT_FUNDS_VERIFIED").ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_interrupt_checkout;");
        (await db.Users.SingleAsync(u => u.Id == CommercialTestSession.UserId)).IsActive = false;
        await db.SaveChangesAsync();
        var startup = new SaleService(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService());
        Assert.Equal(1, await startup.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, await startup.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(1, await db.Sales.CountAsync());
        Assert.Equal(1, await db.InventoryMovements.CountAsync());
        Assert.Equal(1, await db.SalePayments.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.ResolveInterruptedCheckoutAsync("AFTER-APPROVAL", false, true, "Cambiar decisión"));
    }

    private SaleService SalesFor(CommercialOperationContext actor) =>
        new(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), actor);

    private async Task<SaleService> BeginCashierRecoveryAsync(string operation)
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync(u => u.Id == CommercialTestSession.UserId);
        user.RoleString = "CAJERO";
        await db.SaveChangesAsync();
        var cashier = SalesFor(CommercialTestSession.Create(db, role: "CAJERO"));
        await cashier.BeginCheckoutAsync(Sale(operation));
        await cashier.MarkCheckoutFailedAsync(operation, "Interrupción de prueba", true);
        return cashier;
    }

    [Theory]
    [InlineData("GERENTE", false)]
    [InlineData("SUPERVISOR", false)]
    [InlineData("GERENTE", true)]
    public async Task EnterpriseRecovery_SupervisorPreservesOriginalAttributionAndRecordsApproval(string role, bool deactivateCashier)
    {
        await BeginCashierRecoveryAsync("SUPERVISED");
        var approverId = Guid.NewGuid();
        await using (var db = Db())
        {
            if (deactivateCashier)
            {
                (await db.Users.SingleAsync(u => u.Id == CommercialTestSession.UserId)).IsActive = false;
                await db.SaveChangesAsync();
            }
            var supervisor = SalesFor(CommercialTestSession.Create(db, false, userId: approverId, role: role));
            Assert.Single(await supervisor.GetInterruptedCheckoutsAsync());
            await supervisor.ResolveInterruptedCheckoutAsync("SUPERVISED", true, true, "Conteo y documentos comprobados");
            await supervisor.ResolveInterruptedCheckoutAsync("SUPERVISED", true, true, "Conteo y documentos comprobados");
        }
        await using var verify = Db();
        var sale = await verify.Sales.SingleAsync();
        Assert.Equal(CommercialTestSession.UserId.ToString(), sale.CashierUserId);
        Assert.Equal("SHIFT-1", sale.ShiftId);
        Assert.Equal("TEST-REGISTER", sale.RegisterId);
        Assert.Equal(CommercialTestSession.UserId.ToString(), (await verify.SalePayments.SingleAsync()).CashierUserId);
        var approval = await verify.TransactionJournal.SingleAsync(j => j.EventType == "CHECKOUT_FUNDS_VERIFIED");
        Assert.Equal(approverId.ToString(), approval.CashierUserId);
        Assert.Equal("SHIFT-1", approval.ShiftId);
        Assert.Equal(20m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
        Assert.Single(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task EnterpriseRecovery_RejectsCashierAndSupervisorFromAnotherRegister()
    {
        var cashier = await BeginCashierRecoveryAsync("DENIED-RECOVERY");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cashier.ResolveInterruptedCheckoutAsync("DENIED-RECOVERY", true, true, "Prueba"));
        await using var db = Db();
        var otherRegister = SalesFor(CommercialTestSession.Create(db, false, "OTHER-REGISTER", Guid.NewGuid(), "GERENTE"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => otherRegister.ResolveInterruptedCheckoutAsync("DENIED-RECOVERY", true, true, "Prueba"));
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await db.CheckoutOperations.SingleAsync()).State);
    }

    [Fact]
    public async Task EnterpriseRecovery_SupervisorCanDismissUnfundedCashWithoutOpeningOwnShift()
    {
        await BeginCashierRecoveryAsync("NO-FUNDS");
        await using var db = Db();
        var supervisor = SalesFor(CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "SUPERVISOR"));
        await supervisor.ResolveInterruptedCheckoutAsync("NO-FUNDS", false, true, "No se recibió efectivo");
        await supervisor.ResolveInterruptedCheckoutAsync("NO-FUNDS", false, true, "No se recibió efectivo");
        Assert.Equal(CheckoutOperationState.Failed, (await db.CheckoutOperations.SingleAsync()).State);
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Empty(await db.ShiftMovements.ToListAsync());
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "CHECKOUT_NO_FUNDS_VERIFIED").ToListAsync());
    }
}
