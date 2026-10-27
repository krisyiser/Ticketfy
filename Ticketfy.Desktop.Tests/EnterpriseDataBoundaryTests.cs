using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Repositories;
using Ticketfy.Data;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Navigation;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task ApplicationServices_UseAndDisposeIndependentContextsFromTheirInjectedFactory()
    {
        var factory = new ObservedContextFactory(_options);
        Assert.Single(await new UserService(factory, operations: _actor).GetAllAsync());
        Assert.Single(await new UserRepository(factory, operations: _actor).GetActiveUsersAsync());
        Assert.Empty(await new ItemKitService(factory, _actor).GetAllAsync());
        await new PerformanceAnalyticsService(factory, _actor).CalculateTrueCashierProductivityAsync(BusinessTime.Today, BusinessTime.Today);
        Assert.Equal(4, factory.Created.Count);
        Assert.Equal(4, factory.Created.Distinct().Count());
        foreach (var context in factory.Created)
            Assert.Throws<ObjectDisposedException>(() => context.Products.ToList());
    }

    private sealed class ObservedContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public List<AppDbContext> Created { get; } = [];
        public AppDbContext CreateDbContext()
        {
            var context = new AppDbContext(options);
            Created.Add(context);
            return context;
        }
    }

    [Fact]
    public void PresentationBoundaries_CannotAcceptOrRetainSharedDatabaseContexts()
    {
        foreach (var type in new[] { typeof(PosViewModel), typeof(HistoryViewModel), typeof(ProductDialogViewModel), typeof(DialogWirer) })
        {
            Assert.DoesNotContain(type.GetConstructors().SelectMany(c => c.GetParameters()), p => typeof(DbContext).IsAssignableFrom(p.ParameterType));
            Assert.DoesNotContain(type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                f => typeof(DbContext).IsAssignableFrom(f.FieldType));
        }
    }

    [Fact]
    public void CatalogPagingContracts_RequireExplicitDataSourceImplementation()
    {
        foreach (var type in new[] { typeof(IProductService), typeof(ICustomerService), typeof(ISupplierService), typeof(IPurchaseService), typeof(IPromotionService) })
            Assert.True(type.GetMethod("GetPageAsync")!.IsAbstract, type.Name);
        foreach (string method in new[] { "GetPurchasePageAsync", "GetInventoryPageAsync", "GetCategoryCountsAsync" })
            Assert.True(typeof(IProductService).GetMethod(method)!.IsAbstract, method);
    }
}
