using System.Data.Common;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CheckoutWorkload_LegacyListRejectsAnIncompleteUnboundedResult()
    {
        await SeedPendingCheckoutsAsync(205);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.GetInterruptedCheckoutsAsync());
    }

    [Fact]
    public async Task CheckoutWorkload_PendingHeadersDoNotMaterializePaymentPayloads()
    {
        await SeedPendingCheckoutsAsync(100);
        var materialized = new CheckoutMaterializationCounter();
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(materialized).Options;
        Assert.Equal(100, (await Sales.GetInterruptedCheckoutsAsync()).Count);
        Assert.Equal(0, materialized.Count);
    }

    [AvaloniaFact]
    public async Task CheckoutWorkload_ControlStartsWithOneBoundedPageOfPendingHeaders()
    {
        await SeedPendingCheckoutsAsync(205);
        var model = EnterpriseControl();
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(100, model.PendingCheckouts.Count);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("null-item")]
    [InlineData("null-tender")]
    public async Task CheckoutWorkload_OneCorruptIntentDoesNotPreventRecoveryOfAnother(string malformed)
    {
        if (malformed is "null-item" or "null-tender")
        {
            var payload = JsonNode.Parse(UnfundedStartupIntent("RECOVER-BAD").SalePayload)!;
            var invalidArray = new JsonArray(); invalidArray.Add((JsonNode?)null);
            payload[malformed == "null-item" ? "Items" : "Payments"] = invalidArray;
            malformed = payload.ToJsonString();
        }
        var sale = Sale("RECOVER-GOOD") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" };
        await Sales.BeginCheckoutAsync(sale);
        await using (var db = Db())
        {
            CommercialTestSession.ConfirmCard(db, sale.OperationId!, 20m);
            db.CheckoutOperations.Add(new()
            {
                OperationId = "RECOVER-BAD",
                Amount = 20m,
                SalePayload = malformed,
                RegisterId = "TEST-REGISTER",
                ShiftId = "SHIFT-1",
                CashierUserId = CommercialTestSession.UserId.ToString(),
                State = CheckoutOperationState.Started,
                CreatedAtUtc = "2000-01-01T00:00:00Z"
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        var damaged = (await verify.CheckoutOperations.FindAsync("RECOVER-BAD"))!;
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, damaged.State);
        Assert.Equal("El intento no pudo recuperarse automáticamente. Revise datos originales, fondos y permisos en Control comercial; no vuelva a cobrar.",
            damaged.FailureReason);
        Assert.Equal("RECOVER-GOOD", (await verify.Sales.SingleAsync()).OperationId);
        Assert.Single(await verify.SalePayments.ToListAsync());
    }

    [Fact]
    public async Task CheckoutWorkload_ConcurrentCommitCannotBeDowngradedByStartupRecovery()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, actor) =>
        {
            await using (var db = new AppDbContext(options))
            {
                db.Products.Add(new ProductEntity { Id = "P", Name = "Prueba", Price = 10m, Cost = 3m, Stock = 20 });
                await db.SaveChangesAsync();
            }
            var sale = Sale("CONCURRENT-RECOVER") with { CashAmount = 0m, CardAmount = 20m, PaymentMethod = "Tarjeta" };
            var service = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), actor);
            await service.BeginCheckoutAsync(sale);
            await using (var db = new AppDbContext(options)) CommercialTestSession.ConfirmCard(db, sale.OperationId!, 20m);
            var interleave = new CheckoutSelectionInterleave(() => service.SaveAsync(sale));
            var recoveryOptions = new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(interleave).Options;
            var recovery = new SaleService(new TestDbContextFactory(recoveryOptions), new(), new SpyAuditService());
            Assert.Equal(0, await recovery.RecoverInterruptedCheckoutsAsync());
            Assert.True(interleave.Called);
            await using var verify = new AppDbContext(options);
            Assert.Equal(CheckoutOperationState.Completed, (await verify.CheckoutOperations.SingleAsync()).State);
            Assert.Single(await verify.Sales.ToListAsync()); Assert.Single(await verify.SalePayments.ToListAsync());
            Assert.Equal(18d, (await verify.Products.SingleAsync()).Stock);
        });
    }

    private async Task SeedPendingCheckoutsAsync(int count)
    {
        await using var db = Db();
        // Imported pending history may predate the single-open-intent rule.
        for (int i = 0; i < count; i++)
            db.CheckoutOperations.Add(new()
            {
                OperationId = $"PENDING-{i:D4}",
                Amount = 20m,
                SalePayload = new string('x', 4096),
                RegisterId = "TEST-REGISTER",
                ShiftId = "SHIFT-1",
                CashierUserId = CommercialTestSession.UserId.ToString(),
                State = CheckoutOperationState.RequiresReconciliation,
                CreatedAtUtc = "2020-01-01T00:00:00Z"
            });
        await db.SaveChangesAsync();
    }

    private sealed class CheckoutMaterializationCounter : IMaterializationInterceptor
    {
        public int Count;
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        { if (entity is CheckoutOperationEntity) Count++; return entity; }
    }

    private sealed class CheckoutSelectionInterleave(Func<Task> write) : DbCommandInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Called && !command.CommandText.Contains("MAX(", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("FROM \"checkout_operations\"", StringComparison.Ordinal))
            { Called = true; await write(); }
            return result;
        }
    }
}
