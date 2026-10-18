using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private CommercialOperationContext _actor = null!;
    private SaleService Sales => new(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), _actor);
    private AppDbContext Db() => new(_options);
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using var db = Db();
        await db.Database.EnsureCreatedAsync();
        _actor = CommercialTestSession.Create(db);
        db.Products.Add(new ProductEntity { Id = "P", Barcode = "BARCODE", Name = "Producto", Price = 10m, Cost = 3m, Stock = 20, Unit = "Pza" });
        db.Customers.Add(new CustomerEntity { Id = "C", Name = "Cliente", CreditLimit = 1000m });
        db.Suppliers.Add(new SupplierEntity { Id = "S", Name = "Proveedor", IsActive = 1 });
        db.Giftcards.Add(new GiftcardEntity { Id = "G", CardNumber = "WALLET", Balance = 100m, IsActive = true });
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() => await _connection.DisposeAsync();
    private static SaleDto Sale(string op, decimal total = 20m) => new(op, "", [new("P", "Producto", 10m, 99m, 2, "Pza", "General", 0m, 20m)],
        total, 0m, 0m, total, 0m, "Efectivo", null, false, false, null, CashAmount: total, OperationId: op,
        GlobalDiscountAmount: 20m - total);

    [Fact]
    public async Task CashSale_HasExactCostPaymentsActorStockAndImmutableSnapshot()
    {
        var saved = await Sales.SaveAsync(Sale("CASH"));
        await Sales.SaveAsync(Sale("CASH"));
        await using var db = Db();
        var sale = await db.Sales.SingleAsync();
        Assert.Equal(6m, sale.TotalCost);
        Assert.Equal(3m, saved.Items.Single().Cost);
        Assert.Equal("SHIFT-1", sale.ShiftId);
        Assert.Equal(CommercialTestSession.UserId.ToString(), sale.CashierUserId);
        Assert.Equal(20m, (await db.SalePayments.SingleAsync()).Amount);
        Assert.Equal(-2m, (await db.InventoryMovements.SingleAsync()).Quantity);
        Assert.Equal(18d, (await db.Products.SingleAsync()).Stock);
        var payment = await db.SalePayments.SingleAsync();
        payment.Amount = 10m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task MixedPayment_CannotCommitAnUnconfirmedCardCharge()
    {
        var sale = Sale("MIX") with { PaymentMethod = "Mixto", CashAmount = 10m, CardAmount = 10m };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(sale));
        await using var db = Db();
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
        db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
        {
            Id = "T",
            ExternalReference = "MIX",
            IdempotencyKey = "MIX",
            Amount = 10m,
            Status = IntegrationOperationStatus.Completed,
            ProviderPaymentId = "BANK-PAYMENT-1"
        });
        await db.SaveChangesAsync();
        await Sales.SaveAsync(sale);
        Assert.Equal(2, await db.SalePayments.CountAsync());
        Assert.Equal(10m, (await db.ShiftMovements.SingleAsync()).Amount);
    }
    [Fact]
    public async Task MixedWallet_IsDebitedAndReturnedExactlyOnce()
    {
        var saved = await Sales.SaveAsync(Sale("WALLET") with
        {
            PaymentMethod = "Mixto",
            CashAmount = 10m,
            GiftcardAmount = 10m,
            GiftcardNumber = "WALLET"
        });
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Cliente", operationId: "RETURN");
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Cliente", operationId: "RETURN");
        await using var db = Db();
        Assert.Equal(95m, (await db.Giftcards.SingleAsync()).Balance);
        Assert.Equal(20m, (await db.Sales.SingleAsync()).Total);
        Assert.Equal(3m, (await db.ReturnLines.SingleAsync()).CostReversed);
        Assert.Equal(2, await db.PaymentRefunds.CountAsync());
        Assert.Equal(5m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }
    [Fact]
    public async Task DiscountedCreditReturn_RestoresProratedDebtWithoutRewritingSale()
    {
        var saved = await Sales.SaveAsync(Sale("CREDIT", 15m) with { CashAmount = 0, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Cliente", operationId: "R1");
        await using (var db = Db())
        {
            Assert.Equal(7.5m, (await db.Customers.SingleAsync()).Debt);
            Assert.Equal(15m, (await db.Sales.SingleAsync()).Total);
            Assert.Equal(7.5m, (await db.ReturnLines.SingleAsync()).Amount);
        }
        await Sales.CancelSaleAsync(saved.Id, "Resto");
        await using var verify = Db();
        Assert.Equal(0m, (await verify.Customers.SingleAsync()).Debt);
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
        Assert.Equal(15m, (await verify.PaymentRefunds.ToListAsync()).Sum(r => r.Amount));
    }
    [Fact]
    public async Task MissingShiftAndUnauthorizedActor_CannotPostMoney()
    {
        await using var db = Db();
        var shift = await db.Shifts.SingleAsync(); shift.IsOpen = 0; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(Sale("NO-SHIFT")));
        var cashier = CommercialTestSession.Create(db, true, "CASHIER-REGISTER", Guid.NewGuid(), "CAJERO");
        var expenses = new ExpenseService(new TestDbContextFactory(_options), cashier);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => expenses.CreateAsync(new("E", "General", 10m, "", "Gasto", "Efectivo", "admin")));
        Assert.Empty(await db.Expenses.ToListAsync());
    }
    [Fact]
    public async Task PurchaseCashCreditSupplierPaymentAndReturn_Reconcile()
    {
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var dto = new PurchaseDto("BUY", "S", "Proveedor", "F1", "", 0, "", [new("L", "BUY", "P", "Producto", 4m, 3, 12m)],
            PaymentMethod: "Efectivo", PaidAmount: 5m);
        var saved = await purchases.RegisterPurchaseAsync(dto);
        await purchases.RegisterPurchaseAsync(dto);
        Assert.Equal(7m, (await purchases.GetStatementAsync("S")).Balance);
        await purchases.PayAsync(saved.Id, 7m, "Efectivo", null, "PAY");
        await purchases.PayAsync(saved.Id, 7m, "Efectivo", null, "PAY");
        await purchases.ReturnAsync(saved.Items.Single().Id, 1m, "Daño", "SR");
        await purchases.ReturnAsync(saved.Items.Single().Id, 1m, "Daño", "SR");
        await using var db = Db();
        Assert.Equal(22d, (await db.Products.SingleAsync()).Stock);
        Assert.Equal(-4m, (await purchases.GetStatementAsync("S")).Balance);
        Assert.Equal(2, await db.SupplierPayments.CountAsync());
        Assert.Equal(-12m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }
    [Fact]
    public async Task ExpenseCancellation_KeepsBothCashMovementsAndOriginalRecord()
    {
        var expenses = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var dto = new ExpenseDto("E", "General", 10m, "", "Luz", "Efectivo", "admin");
        await expenses.CreateAsync(dto); await expenses.CreateAsync(dto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => expenses.CancelAsync("E", "Error capturado", "VOID"));
        await expenses.CancelAsync("E", "Error capturado", "VOID", reversalVerified: true);
        await expenses.CancelAsync("E", "Error capturado", "VOID", reversalVerified: true);
        await using var db = Db();
        Assert.True((await db.Expenses.SingleAsync()).IsCancelled);
        Assert.Equal(CommercialTestSession.UserId.ToString(), (await db.Expenses.SingleAsync()).RegisteredByUser);
        Assert.Equal(2, await db.ShiftMovements.CountAsync());
        Assert.Equal(0m, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }
    [Fact]
    public async Task Closing_IsAtomicAndDoesNotMixRegisters()
    {
        await Sales.SaveAsync(Sale("FIRST"));
        await using var db = Db();
        var other = CommercialTestSession.Create(db, true, "REGISTER-2", Guid.NewGuid());
        var otherSale = new SaleService(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), other);
        await otherSale.SaveAsync(Sale("SECOND"));
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        var count = new CashupEntity { Id = "COUNT", OperationId = "CLOSE", ClosedCashAmount = 18m };
        var closed = await shifts.CloseWithCashupAsync("SHIFT-1", count);
        await shifts.CloseWithCashupAsync("SHIFT-1", count);
        Assert.Equal(20m, closed.ExpectedBalance); Assert.Equal(-2m, closed.Diff);
        Assert.Equal("SHIFT-1", (await db.Cashups.SingleAsync()).ShiftId);
        Assert.Equal(1, await db.Shifts.CountAsync(s => s.IsOpen == 1));
        Assert.Equal(2, await db.ShiftMovements.CountAsync());
    }
    [Fact]
    public async Task FinancialReports_DoNotDoubleSubtractReturnsOrTreatCreditAsCard()
    {
        var saved = await Sales.SaveAsync(Sale("CREDIT") with { CashAmount = 0m, CustomerId = "C", IsCredit = true, PaymentMethod = "Crédito" });
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Prueba", operationId: "RETURN");
        var expenses = new ExpenseService(new TestDbContextFactory(_options), _actor);
        var summary = await expenses.GetFinancialSummaryAsync(DateTime.Today, DateTime.Today);
        Assert.Equal(10m, summary.TotalRevenue); Assert.Equal(3m, summary.TotalCostOfGoodsSold);
        Assert.Equal(7m, summary.NetProfit); Assert.Equal(0m, summary.CardRevenue);
        var old = await expenses.CalculateTrueNetProfitAsync(DateTime.Today.AddDays(-2), DateTime.Today.AddDays(-1));
        Assert.Equal(0m, old.NetSales);
    }
}
