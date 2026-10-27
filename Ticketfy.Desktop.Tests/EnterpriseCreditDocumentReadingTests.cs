using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task CreditDocuments_PageBothSidesWithWholeAccountTotalsAndNoEntityHydration()
    {
        await using (var db = Db())
        {
            for (int i = 0; i < 205; i++)
            {
                db.AccountEntries.Add(new() { AccountKind = AccountKind.Customer, AccountId = "C", DocumentId = $"SOURCE-{i:D3}", OperationId = "S-" + i, Amount = -10m, CreatedAtUtc = "2026-01-01" });
                db.AccountEntries.Add(new() { AccountKind = AccountKind.Customer, AccountId = "C", DocumentId = $"TARGET-{i:D3}", OperationId = "T-" + i, Amount = 10m, CreatedAtUtc = "2026-01-01" });
            }
            await db.SaveChangesAsync();
        }
        var observer = new HistoryEntitiesProbe();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false).AddInterceptors(observer).Options;
        var service = new AccountCreditSettlementService(new TestDbContextFactory(options), _actor);
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var page = await service.GetDocumentPagesAsync(AccountKind.Customer, "C", i, i);
            Assert.Equal(2050m, page.AvailableCredit); Assert.Equal(2050m, page.Debt);
            Assert.Equal(i < 2, page.HasMoreSources); Assert.Equal(i < 2, page.HasMoreTargets);
            ids.AddRange(page.Sources.Concat(page.Targets).Select(d => d.DocumentId));
        }
        Assert.Equal(410, ids.Distinct().Count()); Assert.Equal(0, observer.Historical);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetDocumentsAsync(AccountKind.Customer, "C"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetDocumentPagesAsync(AccountKind.Customer, "C", -1));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetDocumentPagesAsync(AccountKind.Customer, "C", int.MaxValue));
        var model = EnterpriseControl();
        model.Customer = await CustomersService().GetByIdAsync("C");
        await model.LoadCreditDocumentsCommand.ExecuteAsync(null);
        Assert.Equal(100, model.CreditSources.Count); model.CreditSource = model.CreditSources[0];
        await model.NextCreditTargetsCommand.ExecuteAsync(null);
        Assert.Equal("SOURCE-000", model.CreditSource?.DocumentId);
        model.CreditTarget = model.CreditTargets[0]; model.CreditActionVerified = true;
        await model.NextCreditSourcesCommand.ExecuteAsync(null);
        Assert.Null(model.CreditSource); Assert.Equal("TARGET-100", model.CreditTarget?.DocumentId); Assert.False(model.CreditActionVerified);
        await model.NextCreditSourcesCommand.ExecuteAsync(null); Assert.Equal(5, model.CreditSources.Count);
        await model.PreviousCreditSourcesCommand.ExecuteAsync(null); Assert.Equal(100, model.CreditSources.Count);
        await model.PreviousCreditTargetsCommand.ExecuteAsync(null); Assert.Equal("TARGET-000", model.CreditTargets[0].DocumentId);
        model.Customer = null; Assert.Empty(model.CreditSources); Assert.Empty(model.CreditTargets);
    }

    [Fact]
    public async Task CreditDocuments_ReservesRefundsAndRejectsBrokenKeysWithoutLoadingLargeText()
    {
        string sale = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", sale, 5m, "Efectivo", "Prueba", "DOCUMENT-RESERVED");
        var page = await Credits.GetDocumentPagesAsync(AccountKind.Customer, "C");
        Assert.Equal(5m, page.AvailableCredit); Assert.Equal(-5m, Assert.Single(page.Sources).Balance);
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE account_entries SET DocumentId = {new string('x', 1024 * 1024)}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.GetDocumentPagesAsync(AccountKind.Customer, "C"));
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("blank")]
    [InlineData("date")]
    [InlineData("reservation")]
    [InlineData("missing-source")]
    public async Task CreditDocuments_DamagedEvidenceNeverBecomesUsableDebtOrCredit(string damage)
    {
        string sale = await PaidCustomerReturnAsync();
        await Credits.PrepareRefundAsync(AccountKind.Customer, "C", sale, 5m, "Efectivo", "Prueba", "BAD-DOCUMENT");
        await using var db = Db();
        switch (damage)
        {
            case "nul": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE account_entries SET DocumentId = {"bad\0document"}"); break;
            case "blank": await db.Database.ExecuteSqlRawAsync("UPDATE account_entries SET DocumentId = ' '"); break;
            case "date": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE account_entries SET CreatedAtUtc = {new string('x', 1024 * 1024)}"); break;
            case "reservation": await db.Database.ExecuteSqlRawAsync("UPDATE credit_refund_requests SET Amount = 1100"); break;
            case "missing-source": await db.Database.ExecuteSqlRawAsync("UPDATE credit_refund_requests SET SourceDocumentId = 'MISSING'"); break;
        }
        var before = await ReturnStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits.GetDocumentPagesAsync(AccountKind.Customer, "C"));
        Assert.Equal(before, await ReturnStateAsync());
    }
}
