using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CheckoutStartup_TwoRecoveryServicesPostOneCapturedSale()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, _) =>
        {
            var factory = new TestDbContextFactory(options);
            await CommercialTestSession.PrepareCardCheckoutAsync(factory, "TWO-RECOVERIES", 20m);
            await using (var db = new AppDbContext(options)) CommercialTestSession.ConfirmCard(db, "TWO-RECOVERIES", 20m);
            Task<int> Recover() => Task.Run(() => new SaleService(factory, new(), new SpyAuditService()).RecoverInterruptedCheckoutsAsync());
            int[] results = await Task.WhenAll(Recover(), Recover());
            Assert.Equal(1, results.Sum());
            await using var verify = new AppDbContext(options);
            Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
            Assert.Single(await verify.Sales.ToListAsync()); Assert.Single(await verify.SalePayments.ToListAsync());
            Assert.Single(await verify.InventoryMovements.ToListAsync());
            Assert.Single(await verify.PaymentTerminalOperations.ToListAsync());
        });
    }

    [Fact]
    public async Task CheckoutStartup_BatchesEveryAutomaticCandidateWithoutReadingManualPayloads()
    {
        await SeedPendingCheckoutsAsync(500);
        await using (var db = Db())
        {
            for (int i = 0; i < 205; i++) db.CheckoutOperations.Add(UnfundedStartupIntent($"AUTO-{i:D4}"));
            await db.SaveChangesAsync();
        }
        var materialized = new CheckoutMaterializationCounter();
        var commands = new CheckoutReadObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(materialized, commands).Options;
        var startup = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService());
        Assert.Equal(0, await startup.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(205, materialized.Count);
        Assert.False(commands.UnboundedList);
        Assert.Contains(100, commands.Limits);
        materialized.Count = 0;
        Assert.Equal(0, await startup.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, materialized.Count);
        await using var verify = Db();
        Assert.Equal(205, await verify.CheckoutOperations.CountAsync(o => o.State == CheckoutOperationState.Failed));
        Assert.Equal(500, await verify.CheckoutOperations.CountAsync(o => o.State == CheckoutOperationState.RequiresReconciliation));
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(20d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task CheckoutStartup_CandidateAddedAfterUpperBoundRemainsForNextPass()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, _) =>
        {
            await using (var db = new AppDbContext(options))
            { db.CheckoutOperations.Add(UnfundedStartupIntent("A-ORIGINAL")); await db.SaveChangesAsync(); }
            var insert = new CheckoutSelectionInterleave(async () =>
            {
                await using var db = new AppDbContext(options);
                db.CheckoutOperations.Add(UnfundedStartupIntent("Z-LATER")); await db.SaveChangesAsync();
            });
            var service = new SaleService(new TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(insert).Options), new(), new SpyAuditService());
            Assert.Equal(0, await service.RecoverInterruptedCheckoutsAsync()); Assert.True(insert.Called);
            await using (var verify = new AppDbContext(options))
            {
                Assert.Equal(CheckoutOperationState.Failed, (await verify.CheckoutOperations.FindAsync("A-ORIGINAL"))!.State);
                Assert.Equal(CheckoutOperationState.Started, (await verify.CheckoutOperations.FindAsync("Z-LATER"))!.State);
            }
            Assert.Equal(0, await service.RecoverInterruptedCheckoutsAsync());
            await using var final = new AppDbContext(options);
            Assert.Equal(2, await final.CheckoutOperations.CountAsync(o => o.State == CheckoutOperationState.Failed));
        });
    }

    [Fact]
    public async Task CheckoutStartup_CannotContinueIfStorageCannotPreserveUnresolvedState()
    {
        await using (var db = Db())
        {
            db.CheckoutOperations.Add(UnfundedStartupIntent("STORAGE-FAILURE"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER synthetic_recovery_storage BEFORE UPDATE ON checkout_operations
                BEGIN SELECT RAISE(ABORT, 'synthetic unavailable storage'); END;
                """);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => Sales.RecoverInterruptedCheckoutsAsync());
        await using (var db = Db())
        {
            Assert.Equal(CheckoutOperationState.Started, (await db.CheckoutOperations.SingleAsync()).State);
            Assert.Empty(await db.Sales.ToListAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_recovery_storage;");
        }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.Failed, (await verify.CheckoutOperations.SingleAsync()).State);
    }

    [Fact]
    public async Task CheckoutStartup_InconsistentCommittedIntentIsRepairedWithoutAnotherSale()
    {
        await Sales.BeginCheckoutAsync(Sale("ALREADY-SAVED"));
        await Sales.SaveAsync(Sale("ALREADY-SAVED"));
        await using (var db = Db())
        {
            (await db.CheckoutOperations.SingleAsync()).State = CheckoutOperationState.PaymentAuthorized;
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Single(await verify.Sales.ToListAsync()); Assert.Single(await verify.SalePayments.ToListAsync());
        Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutStartup_UnknownStateOrPayloadIdentityRequiresReviewWithoutMoney(bool unknownState)
    {
        var intent = UnfundedStartupIntent("INVALID-STARTUP");
        if (unknownState) intent.State = (CheckoutOperationState)999;
        else intent.RegisterId = "DIFFERENT-FROM-PAYLOAD";
        await using (var db = Db()) { db.CheckoutOperations.Add(intent); await db.SaveChangesAsync(); }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await verify.CheckoutOperations.SingleAsync()).State);
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.SalePayments.ToListAsync());
    }

    private static CheckoutOperationEntity UnfundedStartupIntent(string operation)
    {
        SaleDto sale = Sale(operation) with
        {
            PaymentMethod = "Tarjeta",
            CashAmount = 0m,
            CardAmount = 20m,
            RegisterId = "TEST-REGISTER",
            ShiftId = "SHIFT-1",
            CashierUserId = CommercialTestSession.UserId.ToString()
        };
        return new()
        {
            OperationId = operation,
            RegisterId = sale.RegisterId,
            ShiftId = sale.ShiftId,
            CashierUserId = sale.CashierUserId,
            Amount = sale.Total,
            PaymentMethod = sale.PaymentMethod,
            SalePayload = JsonSerializer.Serialize(sale, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)
        };
    }
}
