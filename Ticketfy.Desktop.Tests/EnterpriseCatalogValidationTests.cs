using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Avalonia.Headless.XUnit;
using Ticketfy.Core.Services;
using Ticketfy.Core.State;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Data;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("wholesale-precision")]
    [InlineData("money-overflow")]
    [InlineData("tax-precision")]
    [InlineData("stock-overflow")]
    [InlineData("fractional-minimum")]
    [InlineData("fractional-maximum")]
    [InlineData("reorder-nan")]
    [InlineData("reorder-infinite")]
    [InlineData("reorder-negative")]
    [InlineData("reorder-fraction")]
    [InlineData("bulk-reorder-precision")]
    public async Task ProductRejectsValuesThatCannotRoundTripExactly(string invalid)
    {
        var product = (await ProductsService().GetByIdAsync("P"))! with { Id = "INVALID-PRODUCT", Barcode = "INVALID-PRODUCT" };
        product = invalid switch
        {
            "wholesale-precision" => product with { WholesalePrice = 2.345m },
            "money-overflow" => product with { Price = decimal.MaxValue },
            "tax-precision" => product with { TaxRate = 0.165m },
            "stock-overflow" => product with { Stock = double.MaxValue },
            "fractional-minimum" => product with { MinStock = 0.5d },
            "fractional-maximum" => product with { MaxStock = 5.5d },
            "reorder-nan" => product with { ReorderQuantity = double.NaN },
            "reorder-infinite" => product with { ReorderQuantity = double.PositiveInfinity },
            "reorder-negative" => product with { ReorderQuantity = -1d },
            "reorder-fraction" => product with { ReorderQuantity = 0.5d },
            _ => product with { IsBulk = true, ReorderQuantity = 0.0001d }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductsService().AddAsync(product));
        await using var db = Db();
        Assert.Single(await db.Products.ToListAsync());
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task ProductNormalizesKeysBeforeLookupAndPreservesExactValidValues()
    {
        var service = ProductsService();
        var original = (await service.GetByIdAsync("P"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(original with { Id = "DUPLICATE", Barcode = " BARCODE " }));
        await service.BulkSaveAsync([original with { Barcode = " BARCODE ", Name = " Normalized product " }]);
        Assert.Equal("Normalized product", (await service.GetByIdAsync("P"))!.Name);
        await using var db = Db();
        Assert.Single(await db.Products.ToListAsync());
        var bulk = original with
        {
            Id = "PRECISE",
            Barcode = " PRECISE ",
            Name = " Granel ",
            Unit = " kg ",
            IsBulk = true,
            Stock = 1.125d,
            MinStock = 0.125d,
            MaxStock = 5.375d,
            ReorderQuantity = 0.375d,
            Cost = 1.11m,
            Price = 2.23m,
            WholesalePrice = 2.01m,
            WholesaleThreshold = 3,
            DefaultSupplierId = " S "
        };
        await service.AddAsync(bulk);
        var saved = (await service.GetByIdAsync(bulk.Id))!;
        Assert.Equal("PRECISE", saved.Barcode);
        Assert.Equal("Granel", saved.Name);
        Assert.Equal("kg", saved.Unit);
        Assert.Equal(2.01m, saved.WholesalePrice);
        Assert.Equal(0.375d, saved.ReorderQuantity);
        Assert.Equal(1.125d, saved.Stock);
        Assert.Equal(5.375d, saved.MaxStock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(bulk with { Id = "NO-SUPPLIER", Barcode = "NO-SUPPLIER", DefaultSupplierId = "missing" }));
        (await db.Suppliers.SingleAsync()).IsActive = 0;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(bulk with { Id = "INACTIVE-SUPPLIER", Barcode = "INACTIVE-SUPPLIER" }));
        // Keeping an already assigned historical supplier does not invalidate unrelated edits.
        await service.UpdateAsync(saved with { Name = "Renamed bulk" });
    }

    [Fact]
    public async Task ProductEditorOptions_AuthorizesAndBoundsSupplierIdentitiesWithoutContactData()
    {
        await SeedDirectoryPagesAsync();
        await using var db = Db();
        var product = await db.Products.SingleAsync(p => p.Id == "P");
        product.DefaultSupplierId = "S";
        (await db.Suppliers.SingleAsync(s => s.Id == "S")).IsActive = 0;
        await db.SaveChangesAsync();
        var service = ProductsService();
        var options = await service.GetEditorOptionsAsync("P");
        Assert.Equal(100, options.Suppliers.Items.Count);
        Assert.True(options.Suppliers.HasMore);
        Assert.Equal("S", options.AssignedSupplier!.Id);
        Assert.False(options.AssignedSupplier.IsActive);
        var filtered = await service.GetEditorOptionsAsync("P", "Directory 214");
        Assert.Equal("DS-214", Assert.Single(filtered.Suppliers.Items).Id);
        Assert.False(filtered.Suppliers.HasMore);
        Assert.Equal(new[] { "Id", "IsActive", "Name" }, typeof(ProductSupplierOption).GetProperties().Select(p => p.Name).OrderBy(name => name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetEditorOptionsAsync("missing"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProductService(new TestDbContextFactory(_options)).GetEditorOptionsAsync());
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetEditorOptionsAsync("P"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetEditorOptionsAsync());
    }

    [AvaloniaFact]
    public async Task Ui_ProductEditor_PreservesHistoricalAssignmentAndExplicitlySearchesOrClearsSupplier()
    {
        await SeedDirectoryPagesAsync();
        await using var db = Db();
        var original = await db.Products.SingleAsync(p => p.Id == "P");
        original.DefaultSupplierId = "S"; original.Unit = "Kg"; original.IsBulk = false; original.IsActive = false;
        (await db.Suppliers.SingleAsync(s => s.Id == "S")).IsActive = 0;
        await db.SaveChangesAsync();
        var service = ProductsService();
        var model = new ProductDialogViewModel(service);
        model.LoadProductForEdit((await service.GetByIdAsync("P"))!);
        await model.LoadCategoriesAndSuppliersAsync();
        Assert.Equal("S", model.SelectedSupplier!.Id);
        Assert.Contains("inactivo", model.SupplierLookupMessage);
        Assert.False(model.IsBulk);
        model.SupplierSearch = "Directory 214";
        await model.SearchSuppliersCommand.ExecuteAsync(null);
        Assert.Equal("S", model.SelectedSupplier!.Id);
        Assert.Equal(2, model.Suppliers.Count); // Matching supplier plus the retained assignment.
        model.Name = "Renamed without altering supplier";
        var view = new ProductDialog();
        var window = Show(view, model);
        try { await ClickTwiceAsync(view, model.SaveCommand); }
        finally { window.Close(); }
        var preserved = (await service.GetByIdAsync("P"))!;
        Assert.Equal("S", preserved.DefaultSupplierId);
        Assert.False(preserved.IsActive);
        Assert.False(preserved.IsBulk);
        Assert.Empty(model.ErrorMessage);

        var replacement = new ProductDialogViewModel(service);
        replacement.LoadProductForEdit(preserved);
        await replacement.LoadCategoriesAndSuppliersAsync();
        replacement.SupplierSearch = "Directory 214";
        await replacement.SearchSuppliersCommand.ExecuteAsync(null);
        replacement.SelectedSupplier = replacement.Suppliers.Single(s => s.Id == "DS-214");
        await replacement.SaveCommand.ExecuteAsync(null);
        Assert.Equal("DS-214", (await service.GetByIdAsync("P"))!.DefaultSupplierId);
        var clearing = new ProductDialogViewModel(service);
        clearing.LoadProductForEdit((await service.GetByIdAsync("P"))!);
        await clearing.LoadCategoriesAndSuppliersAsync();
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await clearing.LoadCategoriesAndSuppliersAsync(); // Failed options must not silently erase an assignment.
        Assert.Empty(clearing.Suppliers);
        Assert.Null(clearing.SelectedSupplier);
        clearing.ClearSupplierCommand.Execute(null); // Also works when no visible option is selected.
        (await db.Users.SingleAsync()).RoleString = "ADMINISTRADOR";
        await db.SaveChangesAsync();
        await clearing.SaveCommand.ExecuteAsync(null);
        Assert.Empty(clearing.ErrorMessage);
        Assert.Null((await service.GetByIdAsync("P"))!.DefaultSupplierId);
    }

    [AvaloniaFact]
    public async Task ProductEditorLookupCompletingAfterSessionChange_DoesNotRestoreOptions()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var pending = new TaskCompletionSource<ProductEditorOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var products = new DeferredInventoryProducts(ProductsService(), () => throw new NotSupportedException())
        { EditorRead = () => pending.Task };
        var model = new ProductDialogViewModel(products, session);
        var loading = model.LoadCategoriesAndSuppliersAsync();
        session.ClearSession();
        pending.SetResult(new(["Restricted category"], new([new("S", "Restricted supplier", true)], false), null));
        await loading;
        Assert.Empty(model.Suppliers);
        Assert.Empty(model.Categories);
        Assert.Empty(model.SupplierLookupMessage);
    }

    [Fact]
    public async Task CategoryReadIsAuthorizedAndSetBasedRenameRollsBackWithJournalFailure()
    {
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        var categories = new CategoryService(factory, _actor);
        await categories.SaveAsync("COUNT-CAT", "Conteo");
        (await db.Products.SingleAsync()).Category = "Conteo";
        db.Products.AddRange(
            new ProductEntity { Id = "LOWER", Name = "Second", Barcode = "LOWER", Category = "conteo" },
            new ProductEntity { Id = "INACTIVE", Name = "Inactive", Barcode = "INACTIVE", Category = "Conteo", IsActive = false });
        await db.SaveChangesAsync();
        Assert.Equal(2, (await categories.GetAllAsync()).Single(c => c.Id == "COUNT-CAT").ProductCount);
        var original = (await ProductsService().GetByIdAsync("P"))!;
        await new PromotionService(factory, _actor).SaveAsync(new("COUNT-PROMO", "Synthetic", 10m, true, TargetCategory: "Conteo"), "CREATE-COUNT");
        var failOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(new FailCategoryJournal()).Options;
        await Assert.ThrowsAsync<IOException>(() => new CategoryService(new TestDbContextFactory(failOptions), _actor).SaveAsync("COUNT-CAT", "Renamed"));
        Assert.Equal("Conteo", (await ProductsService().GetByIdAsync("P"))!.Category);
        Assert.Equal(original.ExpectedStockVersion, (await ProductsService().GetByIdAsync("P"))!.ExpectedStockVersion);
        Assert.Equal("Conteo", (await new PromotionService(factory, _actor).GetAllAsync()).Single().TargetCategory);
        await categories.SaveAsync("COUNT-CAT", "Renamed");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ProductsService().UpdateAsync(original with { Name = "Stale editor" }));
        Assert.Equal("Renamed", (await ProductsService().GetByIdAsync("INACTIVE"))!.Category);
        (await db.Users.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => categories.GetAllAsync());
    }

    private sealed class FailCategoryJournal : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TransactionJournalEntryEntity>().Any(e => e.Entity.EventType == "CATEGORY_SAVED"))
                throw new IOException("Synthetic journal write failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PromotionFailureFromPreviousTicket_DoesNotBlockNewCart()
    {
        var pending = new TaskCompletionSource<List<CartItemDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousService = new FirstDeferredPromotion(pending.Task);
        var cart = new CartStateStore { PromotionService = previousService };
        await cart.RefreshPromotionsAsync();
        Assert.Equal(0, previousService.Calls); // An empty or signed-out cart does not query promotions.
        cart.PromotionService = null;
        cart.AddItem(new("PREVIOUS", "Previous ticket", 10m, 1, "Pza"), 10);
        cart.PromotionService = previousService;
        var prior = cart.RefreshPromotionsAsync();
        Assert.Equal(1, previousService.Calls);
        cart.Clear();
        Assert.Equal(1, previousService.Calls);
        cart.PromotionService = null;
        cart.AddItem(new("CURRENT", "Current ticket", 20m, 1, "Pza"), 10);
        pending.SetException(new InvalidOperationException("old ticket failure"));
        await prior;
        Assert.Empty(cart.PricingError);
        Assert.Equal("CURRENT", Assert.Single(cart.Items).Id);
        cart.PromotionService = new FirstDeferredPromotion(Task.FromException<List<CartItemDto>>(new InvalidOperationException("current ticket failure")));
        await cart.RefreshPromotionsAsync();
        Assert.Equal("current ticket failure", cart.PricingError);
        cart.PromotionService = null;
        cart.Clear();
        Assert.Empty(cart.PricingError);
    }

    private sealed class FirstDeferredPromotion(Task<List<CartItemDto>> first) : IPromotionService
    {
        private int _calls;
        public int Calls => _calls;
        public Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems) => ++_calls == 1 ? first : Task.FromResult(cartItems);
        public Task<List<PromotionDto>> GetAllAsync() => throw new NotSupportedException();
        public Task<List<PromotionDto>> GetActiveAsync() => throw new NotSupportedException();
        public Task<CatalogPage<PromotionDto>> GetPageAsync(int page, int size = 100, string? search = null) => throw new NotSupportedException();
        public Task SaveAsync(PromotionDto promotion, string operationId) => throw new NotSupportedException();
        public Task SetActiveAsync(string id, bool active, string operationId) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string operationId) => throw new NotSupportedException();
    }
}
