using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("missing-payment")]
    [InlineData("uncaptured-payment")]
    [InlineData("payment-total")]
    [InlineData("missing-stock")]
    [InlineData("wrong-stock-sign")]
    [InlineData("line-total")]
    [InlineData("prior-return")]
    [InlineData("prior-refund")]
    [InlineData("profit")]
    [InlineData("line-cost")]
    [InlineData("payment-actor")]
    [InlineData("stock-document")]
    [InlineData("prior-return-document")]
    [InlineData("prior-return-cost")]
    [InlineData("prior-return-stock")]
    [InlineData("prior-stock-operation")]
    [InlineData("prior-refund-medium")]
    [InlineData("prior-refund-state")]
    [InlineData("prior-refund-operation")]
    [InlineData("prior-refund-missing")]
    [InlineData("prior-foreign-product")]
    [InlineData("prior-lines-missing")]
    public async Task SaleReturn_InconsistentEvidenceCannotPublishAnotherCompensation(string corruption)
    {
        var sale = await Sales.SaveAsync(Sale("ORIGINAL"));
        if (corruption.StartsWith("prior-", StringComparison.Ordinal))
            await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1d, "Primera devolución", operationId: "FIRST-RETURN");
        await using (var db = Db())
        {
            switch (corruption)
            {
                case "missing-payment":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM sale_payments"); break;
                case "uncaptured-payment":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sale_payments SET \"State\" = {(int)TenderState.Authorized}"); break;
                case "payment-total":
                    await db.Database.ExecuteSqlRawAsync("UPDATE sale_payments SET \"Amount\" = 100"); break;
                case "missing-stock":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_movements"); break;
                case "wrong-stock-sign":
                    await db.Database.ExecuteSqlRawAsync("UPDATE inventory_movements SET \"Quantity\" = 2000"); break;
                case "line-total":
                    await db.Database.ExecuteSqlRawAsync("UPDATE sale_lines SET \"Total\" = 1"); break;
                case "prior-return":
                    await db.Database.ExecuteSqlRawAsync("UPDATE return_lines SET \"Amount\" = 1"); break;
                case "prior-refund":
                    await db.Database.ExecuteSqlRawAsync("UPDATE payment_refunds SET \"Amount\" = 1"); break;
                case "profit":
                    await db.Database.ExecuteSqlRawAsync("UPDATE sales SET profit = 1"); break;
                case "line-cost":
                    await db.Database.ExecuteSqlRawAsync("UPDATE sale_lines SET \"CostTotal\" = 1"); break;
                case "payment-actor":
                    await db.Database.ExecuteSqlRawAsync("UPDATE sale_payments SET \"CashierUserId\" = 'otro'"); break;
                case "stock-document":
                    await db.Database.ExecuteSqlRawAsync("UPDATE inventory_movements SET \"DocumentId\" = 'otro'"); break;
                case "prior-return-document":
                    await db.Database.ExecuteSqlRawAsync("UPDATE returns SET total_refunded = 1"); break;
                case "prior-return-cost":
                    await db.Database.ExecuteSqlRawAsync("UPDATE returns SET cogs_reversed = 1"); break;
                case "prior-return-stock":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory_movements WHERE \"Reason\" = 'RETURN'"); break;
                case "prior-stock-operation":
                    await db.Database.ExecuteSqlRawAsync("UPDATE inventory_movements SET \"OperationId\" = 'otro' WHERE \"Reason\" = 'RETURN'"); break;
                case "prior-refund-medium":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment_refunds SET \"Kind\" = {(int)TenderKind.Giftcard}"); break;
                case "prior-refund-state":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment_refunds SET \"State\" = {(int)TenderState.Authorized}"); break;
                case "prior-refund-operation":
                    await db.Database.ExecuteSqlRawAsync("UPDATE payment_refunds SET \"OperationId\" = 'otro'"); break;
                case "prior-refund-missing":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM payment_refunds"); break;
                case "prior-foreign-product":
                    await db.Database.ExecuteSqlRawAsync("UPDATE return_lines SET \"ProductId\" = 'otro'"); break;
                case "prior-lines-missing":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM return_lines"); break;
            }
        }
        var before = await ReturnStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sales.CancelSaleAsync(sale.Id, "Cancelar después de comprobar evidencia"));
        Assert.Equal(before, await ReturnStateAsync());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("lines")]
    [InlineData("payments")]
    [InlineData("header")]
    [InlineData("line-text")]
    [InlineData("nul")]
    public async Task SaleReturn_ExcessiveOriginalCannotPublishACompensation(string component)
    {
        var sale = await Sales.SaveAsync(Sale("BOUNDED-RETURN"));
        await using (var db = Db())
        {
            if (component == "json")
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = {new string(' ', 2 * 1024 * 1024 + 1)}");
            if (component == "header")
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET \"CashierName\" = {new string('é', 2 * 1024 * 1024)}");
            if (component == "line-text")
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sale_lines SET \"Name\" = {new string('é', 2 * 1024 * 1024)}");
            if (component == "nul")
                await db.Database.ExecuteSqlRawAsync("UPDATE sales SET items = CAST(CAST(items AS BLOB) || X'0068696464656E' AS TEXT)");
            if (component == "lines")
            {
                for (int i = 0; i < 1000; i++)
                    db.SaleLines.Add(new SaleLineEntity { SaleId = sale.Id, ProductId = "P", LineNumber = i + 2, Quantity = 1 });
                await db.SaveChangesAsync();
            }
            if (component == "payments")
            {
                for (int i = 0; i < 6; i++)
                    db.SalePayments.Add(new SalePaymentEntity { SaleId = sale.Id, Kind = (TenderKind)i });
                await db.SaveChangesAsync();
            }
        }
        var before = await ReturnStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.CancelSaleAsync(sale.Id, "Original excesivo"));
        Assert.Equal(before, await ReturnStateAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaleReturn_ThousandthsMixedPaymentsAndDamagedGoodsReconcileExactly(bool damaged)
    {
        await using (var db = Db())
        {
            var product = await db.Products.SingleAsync(); product.IsBulk = true; product.Unit = "Kg";
            await db.SaveChangesAsync();
        }
        var sale = await Sales.SaveAsync(Sale("MILLI") with
        {
            Items = [new SaleItemSnapshotDto("P", "Producto", 10m, 3m, .003d, "Kg", "", 0m, .03m)],
            Total = .03m,
            PaidAmount = .03m,
            CashAmount = .01m,
            GiftcardAmount = .02m,
            GiftcardNumber = "WALLET",
            PaymentMethod = "Mixto"
        });
        for (int i = 0; i < 3; i++)
        {
            await Sales.ProcessPartialReturnAsync(sale.Id, "P", .001d, "Milésima", isProductInGoodCondition: !damaged, operationId: "MILLI-" + i);
            await Sales.ProcessPartialReturnAsync(sale.Id, "P", .001d, "Milésima", isProductInGoodCondition: !damaged, operationId: "MILLI-" + i);
        }
        await using var verify = Db();
        Assert.Equal(3, await verify.Returns.CountAsync());
        Assert.Equal(.03m, (await verify.PaymentRefunds.ToListAsync()).Sum(f => f.Amount));
        Assert.Equal(damaged ? 0m : .01m, (await verify.ReturnLines.ToListAsync()).Sum(l => l.CostReversed));
        Assert.Equal(damaged ? 19.997d : 20d, (await verify.Products.SingleAsync()).Stock, 6);
        Assert.Equal(100m, (await verify.Giftcards.SingleAsync()).Balance);
        Assert.Equal(0m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
    }

    [Fact]
    public async Task SaleReturn_ZeroPriceItemsCanReturnWithoutInventingAPayment()
    {
        var sale = await Sales.SaveAsync(Sale("FREE", 0m) with { PaidAmount = 0m, CashAmount = 0m });
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Sin precio", operationId: "FREE-PART");
        await Sales.CancelSaleAsync(sale.Id, "Resto sin precio");
        await using var db = Db();
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
        Assert.Equal(6m, (await db.Returns.ToListAsync()).Sum(r => r.CogsReversed));
    }

    [Fact]
    public async Task SaleReturn_StreamsHistoryWithoutMaterializingReasonsReferencesOrPriorEntities()
    {
        var sale = await Sales.SaveAsync(Sale("STREAM-RETURN"));
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Primera", operationId: "STREAM-FIRST");
        string huge = new('é', 2 * 1024 * 1024);
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE returns SET reason = {huge}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment_refunds SET \"ExternalReference\" = {huge}");
        }
        var observer = new ReturnMaterializationProbe();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false).AddInterceptors(observer).Options;
        var service = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor);
        await service.CancelSaleAsync(sale.Id, "Resto");
        Assert.Equal(0, observer.Prior);
        Assert.Equal(1, observer.Movements); // Only the bounded original stock evidence, never old return moves.
        await using var verify = Db();
        Assert.Equal(20m, (await verify.PaymentRefunds.ToListAsync()).Sum(f => f.Amount));
        Assert.Equal(huge, (await verify.Returns.SingleAsync(r => r.OperationId == "STREAM-FIRST")).Reason);
        Assert.Equal(huge, (await verify.PaymentRefunds.SingleAsync(f => f.OperationId == "STREAM-FIRST")).ExternalReference);
    }

    [Fact]
    public async Task SaleReturn_ReplayDoesNotReadAnOriginalThatBecameUnverifiable()
    {
        var sale = await Sales.SaveAsync(Sale("REPLAY-RETURN"));
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Primera", operationId: "REPLAY-FIRST");
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = {new string(' ', 2 * 1024 * 1024 + 1)}");
        var before = await ReturnStateAsync();
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Primera", operationId: "REPLAY-FIRST");
        Assert.Equal(before, await ReturnStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Otra", operationId: "REPLAY-NEXT"));
        Assert.Equal(before, await ReturnStateAsync());
    }

    private sealed class ReturnMaterializationProbe : IMaterializationInterceptor
    {
        public int Prior, Movements;
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is ReturnEntity or ReturnLineEntity or PaymentRefundEntity or AccountEntryEntity) Prior++;
            if (entity is InventoryMovementEntity) Movements++;
            return entity;
        }
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("credit")]
    [InlineData("wallet")]
    [InlineData("loyalty")]
    [InlineData("card")]
    public async Task SaleReturn_JournalFailureRollsBackMoneyInventoryAndFolioThenRetryCommitsOnce(string tender)
    {
        await using var db = Db();
        (await db.Customers.SingleAsync()).PuntosSaldo = 100;
        await db.SaveChangesAsync();
        if (tender == "card") CommercialTestSession.ConfirmCard(db, "FAIL-RETURN", 20m);
        var draft = Sale("FAIL-RETURN");
        draft = tender switch
        {
            "credit" => draft with { CashAmount = 0m, CustomerId = "C", IsCredit = true, PaymentMethod = "Crédito" },
            "wallet" => draft with { CashAmount = 0m, GiftcardAmount = 20m, GiftcardNumber = "WALLET", PaymentMethod = "Mixto" },
            "loyalty" => draft with { CashAmount = 0m, CustomerId = "C", PaymentMethod = "Puntos de Fidelidad" },
            "card" => draft with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" },
            _ => draft
        };
        var sale = await Sales.SaveAsync(draft);
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Primera", operationId: "FAIL-RETURN-FIRST");
        var before = await ReturnStateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_return_journal BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type = 'SALE_CANCELED'
            BEGIN SELECT RAISE(ABORT, 'synthetic journal failure'); END;
            """);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Sales.CancelSaleAsync(sale.Id, "Resto"));
        Assert.Contains("synthetic journal failure", failure.InnerException?.Message);
        Assert.Equal(before, await ReturnStateAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_return_journal;");
        Assert.True(await Sales.CancelSaleAsync(sale.Id, "Resto"));
        var after = await ReturnStateAsync();
        Assert.False(await Sales.CancelSaleAsync(sale.Id, "Resto"));
        Assert.Equal(after, await ReturnStateAsync());
        Assert.Equal(20d, after.Stock); Assert.Equal(0m, after.Cash); Assert.Equal(0m, after.Debt);
        Assert.Equal(100m, after.Wallet); Assert.Equal(100d, after.Points);
        Assert.Equal(2, after.Returns); Assert.Equal(2, after.Refunds);
    }

    private async Task<(int Returns, int Lines, int Refunds, int Movements, int Accounts, int Journal, int Receipts,
        string Folios, int CashMovements, double Stock, decimal Cash, SaleStatus Status, decimal Debt, double Points, decimal Wallet)> ReturnStateAsync()
    {
        await using var db = Db();
        return (await db.Returns.CountAsync(), await db.ReturnLines.CountAsync(), await db.PaymentRefunds.CountAsync(),
            await db.InventoryMovements.CountAsync(), await db.AccountEntries.CountAsync(), await db.TransactionJournal.CountAsync(),
            await db.CommandReceipts.CountAsync(), string.Join(";", (await db.FolioSequences.OrderBy(f => f.DatePrefix).ToListAsync())
                .Select(f => $"{f.DatePrefix}:{f.LastSequence}")), await db.ShiftMovements.CountAsync(), (await db.Products.SingleAsync(p => p.Id == "P")).Stock,
            (await db.Shifts.SingleAsync()).ExpectedBalance, (await db.Sales.SingleAsync()).Status,
            (await db.Customers.SingleAsync()).Debt, (await db.Customers.SingleAsync()).PuntosSaldo, (await db.Giftcards.SingleAsync()).Balance);
    }
}
