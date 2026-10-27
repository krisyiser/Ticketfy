using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private AccountCreditSettlementService Credits => new(new TestDbContextFactory(_options), _actor);

    private async Task<string> PaidCustomerReturnAsync(string operation = "CREDIT-SOURCE")
    {
        var saved = await Sales.SaveAsync(Sale(operation) with { CashAmount = 0, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        var customers = new CustomerService(new TestDbContextFactory(_options), _actor);
        await customers.RegisterCustomerPaymentAsync("C", 20m, "Efectivo", "Pago", operation + ":PAY");
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Devolución", operationId: operation + ":RETURN");
        return saved.Id;
    }

    private async Task<PurchaseDto> PaidSupplierReturnAsync()
    {
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var purchase = await service.RegisterPurchaseAsync(new PurchaseDto("CREDIT-BUY", "S", "Proveedor", "CREDIT-F1", "", 0, "",
            [new("L", "", "P", "Producto", 4m, 3, 12m)], PaidAmount: 12m, PaymentMethod: "Efectivo"));
        await service.ReturnAsync(purchase.Items.Single().Id, 2m, "Devuelto", "CREDIT-SR");
        return purchase;
    }

    [Fact]
    public async Task CustomerCredit_AppliesToAnotherDocumentWithoutInventingMoneyOrRewritingSales()
    {
        string source = await PaidCustomerReturnAsync();
        var target = await Sales.SaveAsync(Sale("CREDIT-TARGET") with { CashAmount = 0, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        await Credits.ApplyAsync(AccountKind.Customer, "C", source, target.Id, 10m, "Aplicar nota", "APPLY");
        await Credits.ApplyAsync(AccountKind.Customer, "C", source, target.Id, 10m, "Aplicar nota", "APPLY");
        await using var db = Db();
        Assert.Equal(10m, (await db.Customers.SingleAsync()).Debt);
        Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
        Assert.Equal(0m, (await db.AccountEntries.Where(e => e.OperationId == "APPLY").ToListAsync()).Sum(e => e.Amount));
        Assert.Equal(10m, Assert.Single(await Credits.GetDocumentsAsync(AccountKind.Customer, "C")).Balance);
        Assert.All(await db.Sales.ToListAsync(), sale => Assert.Equal(20m, sale.Total));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Customer, "C", source, target.Id, 9m, "Aplicar nota", "APPLY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Customer, "C", source, target.Id, 1m, "Doble crédito", "NEW"));
    }

    [Theory]
    [InlineData("Efectivo")]
    [InlineData("Tarjeta")]
    [InlineData("Transferencia")]
    public async Task CustomerCredit_RefundsExactlyOnceAndReconcilesShift(string method)
    {
        string source = await PaidCustomerReturnAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.RefundAsync(AccountKind.Customer, "C", source, 10m, method, "BANK", "Pagado", "REFUND", false));
        await Credits.RefundAsync(AccountKind.Customer, "C", source, 10m, method, "BANK", "Pagado", "REFUND", true);
        await Credits.RefundAsync(AccountKind.Customer, "C", source, 10m, method, "BANK", "Pagado", "REFUND", true);
        await using var db = Db();
        Assert.Equal(0m, (await db.Customers.SingleAsync()).Debt);
        Assert.Empty(await Credits.GetDocumentsAsync(AccountKind.Customer, "C"));
        Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
        decimal cash = method == "Efectivo" ? 10m : 20m;
        Assert.Equal(cash, (await db.Shifts.SingleAsync()).ExpectedBalance);
        var shift = new ShiftService(new TestDbContextFactory(_options), _actor);
        await shift.CloseWithCashupAsync("SHIFT-1", new CashupEntity { Id = "COUNT", OperationId = "CLOSE", ClosedCashAmount = cash });
        var settlement = await db.AccountCreditSettlements.SingleAsync();
        settlement.Amount = 99m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SupplierCredit_AppliesPartAndReceivesRemainingRefundWithoutChangingPurchase()
    {
        await new ShiftService(new TestDbContextFactory(_options), _actor).RegisterMovementAsync(100m, false, "Fondo adicional", "FUND");
        var source = await PaidSupplierReturnAsync();
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var target = await purchases.RegisterPurchaseAsync(new PurchaseDto("SECOND-BUY", "S", "Proveedor", "CREDIT-F2", "", 0, "",
            [new("L2", "", "P", "Producto", 4m, 1, 4m)]));
        await Credits.ApplyAsync(AccountKind.Supplier, "S", source.Id, target.Id, 4m, "Compensación", "APPLY-S");
        await Credits.ApplyAsync(AccountKind.Supplier, "S", source.Id, target.Id, 4m, "Compensación", "APPLY-S");
        await Credits.RefundAsync(AccountKind.Supplier, "S", source.Id, 4m, "Efectivo", null, "Efectivo recibido", "REFUND-S", true);
        await Credits.RefundAsync(AccountKind.Supplier, "S", source.Id, 4m, "Efectivo", null, "Efectivo recibido", "REFUND-S", true);
        Assert.Equal(0m, (await purchases.GetStatementAsync("S")).Balance);
        await using var db = Db();
        Assert.Equal(92m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(12m, (await db.Purchases.SingleAsync(p => p.Id == source.Id)).TotalCost);
        Assert.Equal(2, await db.AccountCreditSettlements.CountAsync());
        await new ShiftService(new TestDbContextFactory(_options), _actor).CloseWithCashupAsync("SHIFT-1",
            new CashupEntity { Id = "COUNT", OperationId = "CLOSE", ClosedCashAmount = 92m });
    }

    [Fact]
    public async Task AccountCredit_RejectsCrossAccountOverpaymentInvalidMethodsAndUnauthorizedUsers()
    {
        string source = await PaidCustomerReturnAsync();
        var destination = await Sales.SaveAsync(Sale("DEST") with { CashAmount = 0, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Customer, "OTHER", source, destination.Id, 1m, "x", "CROSS"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Customer, "C", source, source, 1m, "x", "SELF"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Customer, "C", source, destination.Id, 11m, "x", "EXCESS"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.RefundAsync(AccountKind.Customer, "C", source, 1m, "unknown", "ref", "x", "BADMETHOD", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.RefundAsync(AccountKind.Customer, "C", source, 1m, "Tarjeta", null, "x", "NOREF", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.ApplyAsync(AccountKind.Giftcard, "C", source, destination.Id, 1m, "x", "KIND"));
        await using var db = Db();
        var cashier = CommercialTestSession.Create(db, register: "OTHER", userId: Guid.NewGuid(), role: "CAJERO");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AccountCreditSettlementService(new TestDbContextFactory(_options), cashier)
            .ApplyAsync(AccountKind.Customer, "C", source, destination.Id, 1m, "x", "NOAUTH"));
        Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        Assert.Empty(await db.CommandReceipts.Where(r => r.Command == "ACCOUNT_CREDIT_SETTLEMENT").ToListAsync());
    }

    [Fact]
    public async Task AccountCredit_RejectsReusedBankReferenceAndRollsBackAllLedgers()
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.RefundAsync(AccountKind.Customer, "C", source, 5m, "Transferencia", "BANK-RECEIPT", "Pagado", "BANK-1", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.RefundAsync(AccountKind.Customer, "C", source, 5m,
            "Tarjeta", "bank-receipt", "Pagado", "BANK-2", true));
        await using var db = Db();
        Assert.Equal(-5m, (await db.Customers.SingleAsync()).Debt);
        Assert.Equal(1, await db.AccountCreditSettlements.CountAsync());
        Assert.False(await db.CommandReceipts.AnyAsync(r => r.OperationId == "BANK-2"));
    }
}
