using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Desktop.Core.Helpers;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task KitCheckout_ChangedNestedRecipeBlocksAuthorizedRecoveryWithoutDuplicatingFundsOrInventory()
    {
        await SeedGraphKitAsync("CHILD", ("P", 1));
        await SeedGraphKitAsync("ROOT", ("CHILD", 1));
        var sale = GraphKitSale("KIT-PENDING", "ROOT") with
        {
            PaymentMethod = "Tarjeta",
            CashAmount = 0,
            CardAmount = 10m,
            CheckoutInventoryFingerprint = "FORGED"
        };
        var sales = Sales; var kits = KitsService();
        await sales.BeginCheckoutAsync(sale);
        string fingerprint;
        await using (var db = Db())
        {
            var intent = await db.CheckoutOperations.SingleAsync();
            fingerprint = JsonSerializer.Deserialize(intent.SalePayload, TicketfyJsonContext.Default.SaleDto)!.CheckoutInventoryFingerprint!;
            Assert.NotNull(fingerprint); Assert.NotEqual("FORGED", fingerprint);
        }
        var child = (await kits.GetForEditAsync("CHILD"))!;
        await kits.SaveAsync(child with { Items = [child.Items[0] with { Quantity = 2 }] }, "CHANGE-RECIPE");
        await sales.BeginCheckoutAsync(sale); // A retry cannot silently re-baseline a pending charge.
        await using (var db = Db())
        {
            Assert.Equal(fingerprint, JsonSerializer.Deserialize((await db.CheckoutOperations.SingleAsync()).SalePayload,
                TicketfyJsonContext.Default.SaleDto)!.CheckoutInventoryFingerprint);
            CommercialTestSession.ConfirmCard(db, "KIT-PENDING", 10m);
        }
        await sales.MarkPaymentAuthorizedAsync("KIT-PENDING", "TEST-BANK:KIT-PENDING");
        Assert.Equal(0, await sales.RecoverInterruptedCheckoutsAsync());
        await using (var db = Db())
        {
            Assert.Equal(CheckoutOperationState.RequiresReconciliation, (await db.CheckoutOperations.SingleAsync()).State);
            Assert.Contains("composición", (await db.CheckoutOperations.SingleAsync()).FailureReason!);
            Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.SalePayments.ToListAsync());
            Assert.Empty(await db.InventoryMovements.ToListAsync());
            Assert.Equal(20, (await db.Products.SingleAsync(p => p.Id == "P")).Stock);
            Assert.Equal(IntegrationOperationStatus.Completed, (await db.PaymentTerminalOperations.SingleAsync()).Status);
        }
        var current = (await kits.GetForEditAsync("CHILD"))!;
        await kits.SaveAsync(current with { Items = [current.Items[0] with { Quantity = 1 }] }, "RESTORE-RECIPE");
        await sales.ResolveInterruptedCheckoutAsync("KIT-PENDING", true, true, "Composición y pago sintéticos verificados");
        await sales.SaveAsync(sale);
        await using var verify = Db();
        Assert.Single(await verify.Sales.ToListAsync()); Assert.Single(await verify.SalePayments.ToListAsync());
        Assert.Equal(-1m, (await verify.InventoryMovements.SingleAsync()).Quantity);
        Assert.Equal(19, (await verify.Products.SingleAsync(p => p.Id == "P")).Stock);
    }

    [Fact]
    public async Task KitCheckout_UnrelatedStockAndLabelChangesDoNotInvalidateThePhysicalComposition()
    {
        await SeedGraphKitAsync("ROOT", ("P", 1));
        var sale = GraphKitSale("STABLE-KIT", "ROOT"); var sales = Sales; var kits = KitsService();
        await sales.BeginCheckoutAsync(sale);
        // A different register can sell while the first register retains its pending intent.
        await using (var otherDb = Db())
            await SalesFor(CommercialTestSession.Create(otherDb, register: "OTHER-REGISTER", userId: Guid.NewGuid()))
                .SaveAsync(Sale("OTHER-CART"));
        var draft = (await kits.GetForEditAsync("ROOT"))!;
        await kits.SaveAsync(draft with { Name = "Renamed only", Description = "New description" }, "RENAME");
        await sales.SaveAsync(sale);
        // Replaying an already committed sale is safe even after its recipe changes.
        draft = (await kits.GetForEditAsync("ROOT"))!;
        await kits.SaveAsync(draft with { Items = [draft.Items[0] with { Quantity = 2 }] }, "AFTER-SALE");
        await sales.SaveAsync(sale);
        await using var db = Db();
        Assert.Equal(2, await db.Sales.CountAsync()); Assert.Equal(17, (await db.Products.SingleAsync(p => p.Id == "P")).Stock);
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task KitCheckout_LegacyPendingKitWithoutFingerprintCannotBeSilentlyReinterpreted()
    {
        await SeedGraphKitAsync("LEGACY", ("P", 1));
        var sale = GraphKitSale("LEGACY-PENDING", "LEGACY"); var sales = Sales;
        await sales.BeginCheckoutAsync(sale);
        await using (var db = Db())
        {
            var intent = await db.CheckoutOperations.SingleAsync();
            var payload = JsonSerializer.Deserialize(intent.SalePayload, TicketfyJsonContext.Default.SaleDto)!;
            intent.SalePayload = JsonSerializer.Serialize(payload with { CheckoutInventoryFingerprint = null }, TicketfyJsonContext.Default.SaleDto);
            await db.SaveChangesAsync();
        }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sales.SaveAsync(sale));
        Assert.Contains("heredado", failure.Message);
        await using var verify = Db();
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
    }

    [Fact]
    public async Task KitCheckout_StartupPreservesSafeActionableReasonForMissingHistoricalComposition()
    {
        await SeedGraphKitAsync("LEGACY", ("P", 1));
        var sale = GraphKitSale("LEGACY-CARD", "LEGACY") with
        { PaymentMethod = "Tarjeta", CashAmount = 0, CardAmount = 10m };
        await Sales.BeginCheckoutAsync(sale);
        await using (var db = Db())
        {
            var intent = await db.CheckoutOperations.SingleAsync();
            var payload = JsonSerializer.Deserialize(intent.SalePayload, TicketfyJsonContext.Default.SaleDto)!;
            intent.SalePayload = JsonSerializer.Serialize(payload with { CheckoutInventoryFingerprint = null },
                TicketfyJsonContext.Default.SaleDto);
            await db.SaveChangesAsync();
            CommercialTestSession.ConfirmCard(db, sale.OperationId!, 10m);
        }
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        await using var verify = Db();
        var pending = await verify.CheckoutOperations.SingleAsync();
        Assert.Equal(CheckoutOperationState.RequiresReconciliation, pending.State);
        Assert.Contains("heredado", pending.FailureReason!);
        Assert.Contains("composición verificada", pending.FailureReason!);
        Assert.Empty(await verify.Sales.ToListAsync()); Assert.Empty(await verify.SalePayments.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(20, (await verify.Products.SingleAsync(p => p.Id == "P")).Stock);
    }

    [Fact]
    public async Task KitCheckout_FlattenedEquivalentNestedRecipeIsStableAcrossComponentOrdering()
    {
        await SeedGraphKitAsync("CHILD-A", ("P", 1));
        await SeedGraphKitAsync("CHILD-B", ("P", 1));
        await SeedGraphKitAsync("ROOT", ("CHILD-A", 1), ("P", 1));
        var sale = GraphKitSale("EQUIVALENT", "ROOT"); var sales = Sales; var kits = KitsService();
        await sales.BeginCheckoutAsync(sale);
        var draft = (await kits.GetForEditAsync("ROOT"))!;
        await kits.SaveAsync(draft with { Items = [new("", "", "P", "", 1), new("", "", "CHILD-B", "", 1)] }, "EQUIVALENT-RECIPE");
        await sales.SaveAsync(sale);
        await using var db = Db();
        Assert.Equal(-2m, (await db.InventoryMovements.SingleAsync()).Quantity);
    }
}
