using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task SupplierLifecycle_PreservesHistoryAndValidatesAdministrativeCommands()
    {
        var suppliers = new SupplierService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => suppliers.CreateAsync(new("EMPTY", "", "", "", "", "", "", true)));
        var created = await suppliers.CreateAsync(new("NEW-S", "Proveedor nuevo", "", "5512345678", "", "Local", "Contacto", true));
        await suppliers.UpdateAsync(created with { Name = "Proveedor actualizado" });
        Assert.Equal("Proveedor actualizado", (await suppliers.GetByIdAsync("NEW-S"))!.Name);
        Assert.True(await suppliers.DeleteAsync("NEW-S"));
        Assert.False((await suppliers.GetByIdAsync("NEW-S"))!.IsActive);
        Assert.DoesNotContain(await suppliers.GetAllAsync(), s => s.Id == "NEW-S");
        Assert.False(await suppliers.DeleteAsync("MISSING"));
        await using var db = Db();
        Assert.Equal(3, await db.TransactionJournal.CountAsync(j => j.EntityId == "NEW-S"));
    }
    [Fact]
    public async Task KitCatalog_UsesSharedComponentsAndPreservesRecipeOnInactivation()
    {
        await using var db = Db();
        var kits = new ItemKitService(db, _actor);
        Assert.True(await kits.SaveAsync("K1", "K1-CODE", "Combo uno", 15m, "", [new("", "K1", "P", "Producto", 2)]));
        Assert.True(await kits.SaveAsync("K2", "K2-CODE", "Combo anidado", 30m, "", [new("", "K2", "K1", "Combo uno", 1), new("", "K2", "P", "Producto", 3)]));
        var products = ProductsService();
        Assert.Equal(4, (await products.GetByIdAsync("K2"))!.Stock);
        Assert.Equal(4, (await products.GetByBarcodeAsync("K2-CODE"))!.Stock);
        Assert.Equal(2, (await kits.GetAllAsync()).Count);
        Assert.Equal(2, (await kits.GetByBarcodeAsync("K2-CODE"))!.Items.Count);
        Assert.Null(await kits.GetByBarcodeAsync("")); Assert.Null(await kits.GetByBarcodeAsync("MISSING"));
        Assert.False(await kits.SaveAsync("K1", "K1-CODE", "Ciclo", 15m, "", [new("", "K1", "K2", "Otro", 1)]));
        Assert.Equal(2, (await kits.GetByBarcodeAsync("K1-CODE"))!.Items.Single().Quantity);
        Assert.True(await kits.DeleteAsync("K2")); Assert.False(await kits.DeleteAsync("MISSING"));
        Assert.Single(await kits.GetAllAsync()); Assert.Null(await kits.GetByBarcodeAsync("K2-CODE"));
        Assert.Equal(3, await db.ItemKitItems.CountAsync());
    }
    [Fact]
    public async Task CustomerProfileAndFiscalData_CannotRewriteBalancesAndSoftDeleteIsAudited()
    {
        var customers = CustomersService();
        var original = (await customers.GetByIdAsync("C"))!;
        await customers.UpdateAsync(original with { Nombre = "Cliente actualizado", Telefono = "5512345678" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.UpdateAsync(original with { Deuda = 1m }));
        await customers.SaveFiscalDataAsync(new("C", "C", "XAXX010101000", "PUBLICO GENERAL", "616", "01000", "S01"));
        Assert.Equal("01000", (await customers.GetFiscalDataAsync("C"))!.CodigoPostal);
        await customers.SaveFiscalDataAsync(new("C", "C", "XAXX010101000", "PUBLICO GENERAL", "616", "02000", "S01"));
        Assert.Equal("02000", (await customers.GetFiscalDataAsync("C"))!.CodigoPostal);
        Assert.Empty(await customers.GetPaymentsAsync("C"));
        await customers.DeleteAsync("C"); await customers.DeleteAsync("C");
        Assert.Empty(await customers.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(Sale("INACTIVE-CUSTOMER") with { CustomerId = "C" }));
    }
    [Fact]
    public async Task InterruptedMixedCheckout_RequiresPhysicalCashReviewThenResumesOnce()
    {
        var sale = Sale("MIXED-POWER") with { PaymentMethod = "Mixto", CashAmount = 10m, CardAmount = 10m };
        await Sales.BeginCheckoutAsync(sale);
        await using var db = Db(); CommercialTestSession.ConfirmCard(db, "MIXED-POWER", 10m);
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Single(await Sales.GetInterruptedCheckoutsAsync());
        Assert.Empty(await db.Sales.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("MIXED-POWER", true, false, "Conteo"));
        await Sales.ResolveInterruptedCheckoutAsync("MIXED-POWER", true, true, "Efectivo contado y cargo verificado");
        await Sales.ResolveInterruptedCheckoutAsync("MIXED-POWER", true, true, "Efectivo contado y cargo verificado");
        Assert.Single(await db.Sales.ToListAsync());
        Assert.Equal(10m, (await db.Shifts.AsNoTracking().SingleAsync()).ExpectedBalance);
        Assert.Empty(await Sales.GetInterruptedCheckoutsAsync());
    }
    [Fact]
    public async Task InterruptedCashWithoutMoney_IsDiscardedOnlyAfterExplicitReview()
    {
        await Sales.BeginCheckoutAsync(Sale("CASH-POWER"));
        Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        Assert.Single(await Sales.GetInterruptedCheckoutsAsync());
        await Sales.ResolveInterruptedCheckoutAsync("CASH-POWER", false, true, "Sin recepción de efectivo");
        await Sales.ResolveInterruptedCheckoutAsync("CASH-POWER", false, true, "Sin recepción de efectivo");
        Assert.Empty(await Sales.GetInterruptedCheckoutsAsync());
        await using var db = Db(); Assert.Empty(await db.Sales.ToListAsync());
        Assert.Equal(20, (await db.Products.SingleAsync()).Stock);
    }
    [Fact]
    public async Task LegacyTurnRequiresExplicitCountAndKeepsUnknownOriginalCashier()
    {
        await using var db = Db();
        var current = await db.Shifts.SingleAsync(); current.IsOpen = 0;
        db.Shifts.Add(new ShiftEntity { Id = "LEGACY", IsOpen = 1, OpeningBalance = 40m, ExpectedBalance = 40m, StartTime = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        Assert.Single(await shifts.GetLegacyOpenAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => shifts.OpenAsync(40m, "AFTER-LEGACY"));
        await shifts.ReconcileLegacyAsync("LEGACY", 39m, "Acta de conteo 001", "LEGACY-COUNT");
        await shifts.ReconcileLegacyAsync("LEGACY", 39m, "Acta de conteo 001", "LEGACY-COUNT");
        var old = await db.Shifts.AsNoTracking().SingleAsync(s => s.Id == "LEGACY");
        Assert.Null(old.CashierUserId); Assert.Null(old.RegisterId); Assert.Equal(-1m, old.Diff);
        await shifts.OpenAsync(39m, "AFTER-LEGACY");
        Assert.Empty(await shifts.GetLegacyOpenAsync());
    }
    [Fact]
    public async Task CategoryRenameMovesProductsAndPromotionsAtomicallyAndIsAudited()
    {
        var factory = new TestDbContextFactory(_options);
        var categories = new CategoryService(factory, _actor);
        await categories.SaveAsync("CAT", "General viejo");
        var products = ProductsService();
        await products.UpdateAsync((await products.GetByIdAsync("P"))! with { Category = "General viejo" });
        var promotions = new PromotionService(factory, _actor);
        await promotions.SaveAsync(new("CAT-PROMO", "Promo categoría", 10m, true, TargetCategory: "General viejo"));
        await categories.SaveAsync("CAT", "Nuevo");
        Assert.Equal("Nuevo", (await products.GetByIdAsync("P"))!.Category);
        Assert.Equal("Nuevo", (await promotions.GetAllAsync()).Single().TargetCategory);
        await categories.DeleteAsync("CAT");
        Assert.Equal("General", (await products.GetByIdAsync("P"))!.Category);
        Assert.False((await promotions.GetAllAsync()).Single().IsActive);
        await using var db = Db();
        Assert.Equal(3, await db.TransactionJournal.CountAsync(j => j.EntityId == "CAT"));
        var cashier = CommercialTestSession.Create(db, false, "READONLY", Guid.NewGuid(), "CAJERO");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CategoryService(factory, cashier).SaveAsync("BLOCKED", "No autorizado"));
    }
    [Fact]
    public async Task CashierReport_UsesOwnNetSalesAttendanceAndNoInventedCommission()
    {
        var saved = await Sales.SaveAsync(Sale("PERFORMANCE"));
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Cliente", operationId: "PERFORMANCE-RETURN");
        await using var db = Db();
        db.Attendances.Add(new AttendanceEntity { UserId = CommercialTestSession.UserId, CheckInTime = DateTime.Today.AddHours(8), CheckOutTime = DateTime.Today.AddHours(10) });
        await db.SaveChangesAsync();
        var service = new PerformanceAnalyticsService(db, _actor);
        var row = Assert.Single(await service.CalculateTrueCashierProductivityAsync(DateTime.Today, DateTime.Today));
        Assert.Equal(10m, row.GrossSales); Assert.Equal(2, row.TotalHoursWorked);
        Assert.Equal(5m, row.TrueSalesPerHour); Assert.Equal(0m, row.EstimatedCommission);
        Assert.Equal(10m, row.AverageTicketValue);
    }
    [Fact]
    public async Task StaleCatalogEdit_CannotOverwriteStockOrNewerPrice()
    {
        var products = ProductsService();
        var old = (await products.GetByIdAsync("P"))!;
        await products.UpdateAsync(old with { Price = 11m });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => products.UpdateAsync(old with { Price = 12m }));
        var beforeSale = (await products.GetByIdAsync("P"))!;
        await products.UpdateAsync(beforeSale with { Stock = 19, StockAdjustmentReason = "Merma comprobada" });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => products.UpdateAsync(beforeSale with { Name = "Cambio obsoleto" }));
        Assert.Equal(19, (await products.GetByIdAsync("P"))!.Stock);
    }
    [Fact]
    public async Task CsvInvalidMoney_RejectsWholeImportInsteadOfCreatingFreeProducts()
    {
        var products = ProductsService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync("barcode,name,cost,price,stock\nOK,Bueno,2,3,4\nBAD,Malo,error,5,4"));
        Assert.Null(await products.GetByBarcodeAsync("OK"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.ImportFromCsvTextAsync("123,Nombre,1,2"));
    }
    [Fact]
    public async Task CashupBreakdown_DoesNotTreatSupplierPaymentsAsNegativeSales()
    {
        await Sales.SaveAsync(Sale("CASHUP-SALE"));
        var expenses = new ExpenseService(new TestDbContextFactory(_options), _actor);
        await expenses.CreateAsync(new("CASHUP-E", "General", 5m, "", "Gasto", "Efectivo", ""));
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        await shifts.CloseAsync("SHIFT-1", 15m);
        var cashup = Assert.Single(await shifts.GetCashupsAsync(DateTime.Today, DateTime.Today));
        Assert.Equal(20m, cashup.TotalSales); Assert.Equal(5m, cashup.TotalExpenses);
        var reconciliation = new CommercialReconciliationService(new TestDbContextFactory(_options), _actor);
        Assert.True(Assert.Single(await reconciliation.ReconcileAsync(DateTime.Today)).IsBalanced);
    }
    [Fact]
    public async Task CloseRejectsMissingCashDocumentEvenWhenGrandTotalsMatch()
    {
        await Sales.SaveAsync(Sale("CHECK-CASH"));
        await using var db = Db();
        // Deliberate corruption in an EnsureCreated-only test database. Production triggers forbid this deletion.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM shift_movements");
        var shifts = new ShiftService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shifts.CloseAsync("SHIFT-1", 0m));
        Assert.Equal(1, (await db.Shifts.SingleAsync()).IsOpen);
        Assert.Empty(await db.Cashups.ToListAsync());
    }
    [Fact]
    public async Task RewardsAreReversedForTheReturnedLine_NotAnAverageAcrossProducts()
    {
        await using var db = Db();
        var product = await db.Products.SingleAsync(); product.PointsRewarded = 5;
        db.Products.Add(new ProductEntity { Id = "NO-POINTS", Barcode = "NO-POINTS", Name = "Sin puntos", Price = 10, Stock = 10, Unit = "Pza" });
        await db.SaveChangesAsync();
        var sale = Sale("LINE-POINTS") with { CustomerId = "C", Items = [new("P", "P", 10m, 0m, 1, "Pza", "General", 0m, 10m), new("NO-POINTS", "N", 10m, 0m, 1, "Pza", "General", 0m, 10m)] };
        var saved = await Sales.SaveAsync(sale);
        await Sales.ProcessPartialReturnAsync(saved.Id, "NO-POINTS", 1, "Sin puntos", operationId: "NO-POINTS-RETURN");
        Assert.Equal(5, (await db.Customers.AsNoTracking().SingleAsync()).PuntosSaldo);
        await Sales.ProcessPartialReturnAsync(saved.Id, "P", 1, "Con puntos", operationId: "POINTS-RETURN");
        Assert.Equal(0, (await db.Customers.AsNoTracking().SingleAsync()).PuntosSaldo);
    }
}
