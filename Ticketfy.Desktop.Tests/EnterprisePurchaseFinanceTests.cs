using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private PurchaseService PurchaseFinanceService() => new(new TestDbContextFactory(_options), _actor);

    private async Task<string> ReceiveFinancialPurchaseAsync(string id = "FIN-BUY", decimal paid = 0m)
    {
        var purchase = await PurchaseFinanceService().RegisterPurchaseAsync(new(id, "S", "Proveedor", id, "", 0m, "",
            [new("", "", "P", "Producto", 3m, 2, 6m)], OperationId: id, PaidAmount: paid, PaymentMethod: "Efectivo"));
        return purchase.Id;
    }

    [Fact]
    public async Task PurchaseFinance_ReadsRecordedPaymentsInsteadOfDefaultingToCreditAndZero()
    {
        string id = await ReceiveFinancialPurchaseAsync(paid: 2m);
        await PurchaseFinanceService().PayAsync(id, 1m, "Tarjeta", "SYNTHETIC-BANK", "FIN-PAY");
        var purchase = (await PurchaseFinanceService().GetByIdAsync(id))!;
        Assert.Equal(3m, purchase.PaidAmount);
        Assert.NotEqual("Crédito", purchase.PaymentMethod);
        Assert.NotNull(purchase.Financial);
        Assert.Equal(3m, purchase.Financial.Balance);
        Assert.Equal(2m, purchase.Financial.CashPayments);
        Assert.Equal(1m, purchase.Financial.CardPayments);
        var first = await PurchaseFinanceService().GetFinancialHistoryAsync(id, 0, 2);
        Assert.Equal(3, first.TotalCount); Assert.True(first.HasMore);
        Assert.Equal(6m, first.Items[0].Amount);
        Assert.Equal(-2m, first.Items[1].Amount);
        var last = await PurchaseFinanceService().GetFinancialHistoryAsync(id, 1, 2);
        var payment = Assert.Single(last.Items);
        Assert.False(last.HasMore); Assert.Equal("SYNTHETIC-BANK", payment.Reference);
        Assert.Equal("Tarjeta", payment.Method); Assert.Equal(-1m, payment.Amount);
        Assert.Equal("SHIFT-1", payment.ShiftId); Assert.Equal("TEST-REGISTER", payment.RegisterId);
        Assert.Equal(3m, last.Summary!.Balance);
        await PurchaseFinanceService().PayAsync(id, 3m, "Transferencia", "TRANSFER", "FIN-TRANSFER");
        purchase = (await PurchaseFinanceService().GetByIdAsync(id))!;
        Assert.Equal(0m, purchase.Financial!.Balance); Assert.Equal(3m, purchase.Financial.TransferPayments);
        Assert.Equal("Liquidada", purchase.FinancialStatus);
        Assert.Contains("Transferencia", purchase.PaymentMethod);
    }

    [Fact]
    public async Task PurchaseFinance_ControlCharactersInStoredLineAreNotPrintableEvidence()
    {
        string id = await ReceiveFinancialPurchaseAsync();
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PurchaseItems SET ProductName = {"Producto\u001b@impresión"} WHERE PurchaseId = {id}");
        var purchase = (await PurchaseFinanceService().GetByIdAsync(id))!;
        Assert.False(purchase.DetailsAvailable);
    }

    [Fact]
    public async Task PurchaseFinance_DistinguishesReturnsCreditApplicationsAndReservedRefunds()
    {
        var service = PurchaseFinanceService();
        var source = await PaidSupplierReturnAsync(); // 12 paid, 8 returned, -8 credit.
        string target = await ReceiveFinancialPurchaseAsync();
        await Credits.ApplyAsync(AccountKind.Supplier, "S", source.Id, target, 4m, "Compensación", "FIN-APPLY");
        await Credits.PrepareRefundAsync(AccountKind.Supplier, "S", source.Id, 2m, "Efectivo", "Prueba", "FIN-RESERVE");
        var doc = (await service.GetByIdAsync(source.Id))!;
        Assert.Equal(12m, doc.Financial!.Payments); Assert.Equal(8m, doc.Financial.Returns);
        Assert.Equal(4m, doc.Financial.CreditTransferred); Assert.Equal(-4m, doc.Financial.Balance);
        Assert.Equal(2m, doc.Financial.ReservedCredit); Assert.Equal(2m, doc.Financial.AvailableCredit);
        Assert.Equal(2m, doc.Items.Single().ReturnedQuantity); Assert.Equal(1m, doc.Items.Single().ReturnableQuantity);
        var targetDoc = (await service.GetByIdAsync(target))!;
        Assert.Equal(0m, targetDoc.PaidAmount); Assert.Equal(4m, targetDoc.Financial!.CreditApplied);
        Assert.Equal(2m, targetDoc.Financial.Balance);
        await Credits.ResolveRefundAsync("FIN-RESERVE", true, null, "Efectivo recibido en prueba", true);
        var history = await service.GetFinancialHistoryAsync(source.Id);
        Assert.Equal(2m, history.Summary!.CreditRefunded); Assert.Equal(0m, history.Summary.ReservedCredit);
        Assert.Equal(-2m, history.Summary.Balance); Assert.Equal(12m, history.Summary.Payments);
        var refund = Assert.Single(history.Items, e => e.EntryType == "CREDIT_REFUNDED");
        Assert.Equal("Efectivo recibido", refund.Method); Assert.Equal(2m, refund.Amount);
        Assert.Equal(history.Summary, history.Document!.Financial);
    }

    [Theory]
    [InlineData("Tarjeta")]
    [InlineData("Transferencia")]
    public async Task PurchaseFinance_BankRefundIsASeparateCreditMovement(string method)
    {
        var source = await PaidSupplierReturnAsync();
        await Credits.RefundAsync(AccountKind.Supplier, "S", source.Id, 8m, method, "SYNTHETIC-REFUND", "Recibido", "FIN-REFUND", true);
        var page = await PurchaseFinanceService().GetFinancialHistoryAsync(source.Id);
        Assert.NotNull(page.Summary); Assert.Equal(0m, page.Summary.Balance);
        Assert.Equal(12m, page.Summary.Payments); Assert.Equal(8m, page.Summary.CreditRefunded);
        Assert.Equal(method, Assert.Single(page.Items, e => e.EntryType == "CREDIT_REFUNDED").Method);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("journal")]
    [InlineData("account")]
    [InlineData("owner")]
    [InlineData("reference")]
    [InlineData("date")]
    [InlineData("original-stock")]
    [InlineData("return-stock")]
    [InlineData("return-line")]
    [InlineData("return-amount")]
    public async Task PurchaseFinance_DamagedEvidenceIsUnknownAndNewCommandsRollBack(string damage)
    {
        var service = PurchaseFinanceService();
        string id = await ReceiveFinancialPurchaseAsync(paid: 1m);
        string line = (await service.GetByIdAsync(id))!.Items.Single().Id;
        await service.ReturnAsync(line, 0.5m, "Prueba parcial", "FIN-RETURN");
        await using var db = Db();
        switch (damage)
        {
            case "cash": await db.Database.ExecuteSqlRawAsync("DELETE FROM shift_movements WHERE OperationId='FIN-BUY:PAY'"); break;
            case "journal": await db.Database.ExecuteSqlRawAsync("DELETE FROM transaction_journal WHERE event_type='SUPPLIER_PAYMENT'"); break;
            case "account": await db.Database.ExecuteSqlRawAsync("DELETE FROM account_entries WHERE EntryType='SUPPLIER_PAYMENT'"); break;
            case "owner": await db.Database.ExecuteSqlRawAsync("UPDATE supplier_payments SET CashierUserId='OTHER'"); break;
            case "reference": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supplier_payments SET Reference={new string('é', 1024 * 1024)}"); break;
            case "date": await db.Database.ExecuteSqlRawAsync("UPDATE supplier_payments SET CreatedAtUtc='invalid'"); break;
            case "original-stock": await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_movements WHERE Reason='PURCHASE'"); break;
            case "return-stock": await db.Database.ExecuteSqlRawAsync("UPDATE inventory_movements SET Quantity=-1 WHERE Reason='SUPPLIER_RETURN'"); break;
            case "return-line": await db.Database.ExecuteSqlRawAsync("UPDATE supplier_returns SET PurchaseItemId='missing'"); break;
            case "return-amount": await db.Database.ExecuteSqlRawAsync("UPDATE supplier_returns SET Amount=1"); break;
        }
        var probe = new PurchaseDocumentProbe();
        var observed = new PurchaseService(new TestDbContextFactory(ObservedPurchaseOptions(probe)), _actor);
        var doc = (await observed.GetByIdAsync(id))!;
        Assert.Null(doc.Financial); Assert.Equal("No calculado", doc.BalanceDisplay);
        Assert.Contains("no verificable", doc.FinancialStatus);
        var page = await observed.GetFinancialHistoryAsync(id);
        Assert.Null(page.Summary); Assert.NotEmpty(page.Warning);
        Assert.All(page.Items, e => Assert.True(e.Reference.Length <= 800));
        var before = await FinancialPurchaseStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PayAsync(id, 1m, "Efectivo", null, "FIN-BAD-PAY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnAsync(line, 0.5m, "Nuevo", "FIN-BAD-RETURN"));
        Assert.Equal(before, await FinancialPurchaseStateAsync());
        // Replaying the already committed command must not create another return, even if its evidence is damaged.
        if (damage != "return-line") await service.ReturnAsync(line, 0.5m, "Prueba parcial", "FIN-RETURN");
        Assert.Equal(before, await FinancialPurchaseStateAsync());
        Assert.Equal(0, probe.Payments); Assert.Equal(0, probe.Returns); Assert.Equal(0, probe.Accounts);
    }

    private async Task<(int Payments, int Returns, int Accounts, int Stock, int Journal, int Receipts, double Quantity, decimal Cash)> FinancialPurchaseStateAsync()
    {
        await using var db = Db();
        return (await db.SupplierPayments.CountAsync(), await db.SupplierReturns.CountAsync(), await db.AccountEntries.CountAsync(),
            await db.InventoryMovements.CountAsync(), await db.TransactionJournal.CountAsync(), await db.CommandReceipts.CountAsync(),
            (await db.Products.SingleAsync()).Stock, (await db.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70)]
    public async Task PurchaseFinance_LegacyReconciliationPreservesUnknownOriginalPaymentsAndRequiresAudit(int confirmed)
    {
        await SeedHistoricalPurchaseAsync();
        var service = PurchaseFinanceService();
        Assert.Null((await service.GetByIdAsync("OLD-PURCHASE"))!.Financial);
        var preview = await Historical.PreviewAsync(HistoricalBalanceScope.SupplierPurchase, "OLD-PURCHASE");
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, confirmed, preview.Fingerprint, "ACTA", "Revisión sintética", true, "FIN-LEGACY");
        var doc = (await service.GetByIdAsync("OLD-PURCHASE"))!;
        Assert.False(doc.DetailsAvailable); Assert.NotNull(doc.Financial);
        Assert.True(doc.Financial.IsHistorical); Assert.Equal(confirmed, doc.Financial.Balance);
        Assert.Equal(0m, doc.PaidAmount); Assert.Contains("anteriores incompletos", doc.FinancialStatus);
        if (confirmed > 0)
        {
            await service.PayAsync(doc.Id, 20m, "Efectivo", null, "FIN-OLD-PAY");
            doc = (await service.GetByIdAsync(doc.Id))!;
            Assert.Equal(50m, doc.Financial!.Balance); Assert.Equal(20m, doc.Financial.Payments);
        }
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM transaction_journal WHERE event_type='HISTORICAL_BALANCE_RECONCILED'");
        Assert.Null((await service.GetByIdAsync(doc.Id))!.Financial);
    }

    [Fact]
    public async Task PurchaseFinance_RejectsInvalidPagingAndMissingDocument()
    {
        var service = PurchaseFinanceService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetFinancialHistoryAsync("bad\0key"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetFinancialHistoryAsync("P", -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetFinancialHistoryAsync("P", 0, 101));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetFinancialHistoryAsync("P", int.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetFinancialHistoryAsync("MISSING"));
    }

    [Fact]
    public async Task PurchaseFinance_PagesBeyondOneHundredEntriesWithWholeDocumentBalance()
    {
        var service = PurchaseFinanceService();
        string id = await ReceiveFinancialPurchaseAsync();
        for (int i = 0; i < 104; i++)
            await service.PayAsync(id, 0.01m, "Efectivo", null, "SMALL-PAY-" + i);
        var first = await service.GetFinancialHistoryAsync(id);
        Assert.Equal(100, first.Items.Count); Assert.Equal(105, first.TotalCount);
        Assert.True(first.HasMore); Assert.Equal(4.96m, first.Summary!.Balance);
        var second = await service.GetFinancialHistoryAsync(id, 1);
        Assert.Equal(5, second.Items.Count); Assert.False(second.HasMore);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(105, first.Items.Concat(second.Items).Select(e => e.Sequence).Distinct().Count());
        Assert.Empty((await service.GetFinancialHistoryAsync(id, 2)).Items);
    }

    [Theory]
    [InlineData("cash-reference")]
    [InlineData("bank-reference")]
    [InlineData("catalog-name")]
    [InlineData("invoice")]
    public async Task PurchaseFinance_NewInvalidEvidenceCannotCommit(string field)
    {
        var service = PurchaseFinanceService();
        if (field.EndsWith("reference", StringComparison.Ordinal))
        {
            string id = await ReceiveFinancialPurchaseAsync();
            var before = await FinancialPurchaseStateAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PayAsync(id, 1m,
                field == "cash-reference" ? "Efectivo" : "Tarjeta", "BANK\u001bINJECTED", "INVALID-REFERENCE"));
            Assert.Equal(before, await FinancialPurchaseStateAsync());
        }
        else
        {
            await using var db = Db();
            if (field == "catalog-name") await db.Database.ExecuteSqlRawAsync("UPDATE products SET name='Invalid'||char(27)");
            var before = await FinancialPurchaseStateAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterPurchaseAsync(new("BAD", "S", "Proveedor",
                field == "invoice" ? "INV\u001b" : "INV", "", 0m, "", [new("", "", "P", "Producto", 3m, 2, 6m)])));
            Assert.Equal(before, await FinancialPurchaseStateAsync());
            Assert.Empty(await db.Purchases.ToListAsync());
        }
    }

    [Fact]
    public async Task PurchaseFinance_MaximumCommandIdentitySupportsItsDerivedPayment()
    {
        string id = await ReceiveFinancialPurchaseAsync(new string('x', 128), 1m);
        var purchase = (await PurchaseFinanceService().GetByIdAsync(id))!;
        Assert.Equal(5m, purchase.Financial!.Balance); Assert.Equal(1m, purchase.PaidAmount);
    }

    [Fact]
    public async Task PurchaseFinance_IndexMigrationPreservesHistoricalAmountsAndPrecision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261024035001_IndexSaleReturnEvidence");
        db.Purchases.Add(new() { Id = "HISTORICAL", SupplierId = "S", TotalCost = 12.34m, LedgerVersion = 0 });
        db.PurchaseItems.Add(new() { Id = "LINE", PurchaseId = "HISTORICAL", ProductId = "P", Quantity = 1.234, UnitPrice = 10m, TotalPrice = 12.34m });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(); await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(12.34m, (await db.Purchases.SingleAsync()).TotalCost);
        Assert.Equal(1.234, (await db.PurchaseItems.SingleAsync()).Quantity);
        Assert.Empty(await db.AccountEntries.ToListAsync());
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='index'").ToListAsync();
        foreach (string index in new[] { "IX_supplier_returns_PurchaseId", "IX_supplier_returns_PurchaseItemId",
            "IX_supplier_payments_PurchaseId", "IX_PurchaseItems_PurchaseId_ProductId_UnitPrice", "IX_inventory_movements_OperationId",
            "IX_account_entries_AccountKind_DocumentId_PostingSequence", "IX_account_credit_settlements_AccountKind_SourceDocumentId",
            "IX_account_credit_settlements_AccountKind_TargetDocumentId" }) Assert.Contains(index, indexes);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
