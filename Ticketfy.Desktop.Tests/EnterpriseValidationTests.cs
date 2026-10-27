using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("Desconocido")]
    [InlineData("")]
    [InlineData("Efectivoo")]
    public async Task EnterpriseValidation_ExpenseRejectsUnknownTenderEvenWithReference(string method)
    {
        var service = new ExpenseService(new TestDbContextFactory(_options), _actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new ExpenseDto("INVALID-MEDIUM", "General", 10m, "", "Gasto", method, "ignored", Reference: "TEST-REFERENCE")));
        await using var db = Db();
        Assert.Empty(await db.Expenses.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task EnterpriseValidation_PromotionGeneratedIdIsRecordedInAudit()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        await service.SaveAsync(new PromotionDto("", "Promoción sin ID", 10m, true), "CREATE-GENERATED");
        await using var db = Db();
        var promotion = await db.Promotions.SingleAsync();
        var journal = await db.TransactionJournal.SingleAsync(j => j.EventType == "PROMOTION_SAVED");
        Assert.False(string.IsNullOrWhiteSpace(promotion.Id));
        Assert.Equal(promotion.Id, journal.EntityId);
    }

    [Fact]
    public async Task EnterpriseValidation_SensitiveReadsRequireAuthenticatedContext()
    {
        var factory = new TestDbContextFactory(_options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CustomerService(factory).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CustomerService(factory).GetByIdAsync("C"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CustomerService(factory).GetFiscalDataAsync("C"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SupplierService(factory).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProductService(factory).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PromotionService(factory).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GiftcardService(factory).GetAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SaleService(factory, new CoOccurrenceQueue(), new SpyAuditService()).GetByIdAsync("unknown"));
    }
}
