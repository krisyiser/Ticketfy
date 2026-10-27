using Ticketfy.Data.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public sealed record ProductCatalogPage(List<ProductDto> Items, bool HasMore);
public sealed record ProductCategoryCount(string Name, int Count);
public sealed record InventoryCatalogPage(List<ProductDto> Items, bool HasMore, decimal StockCost, decimal StockSale, bool CostsVisible);
public sealed record ProductSupplierOption(string Id, string Name, bool IsActive);
public sealed record ProductEditorOptions(List<string> Categories, CatalogPage<ProductSupplierOption> Suppliers, ProductSupplierOption? AssignedSupplier);

/// <summary>
/// Product CRUD operations with pagination, barcode lookup, and CSV bulk import.
/// </summary>
public interface IProductService
{
    Task<ProductEditorOptions> GetEditorOptionsAsync(string? productId = null, string? supplierSearch = null) => throw new NotSupportedException();
    Task<List<Ticketfy.Data.Entities.InventoryMovementEntity>> GetKardexAsync(string productId) => throw new System.NotSupportedException();
    Task<InventoryLedgerPageDto> GetKardexPageAsync(string productId, int page = 0, int size = 100, LedgerReadCut? cut = null) => throw new NotSupportedException();
    /// <summary>Compatibility read, limited to 200 active products. Larger catalogs require GetPageAsync.</summary>
    Task<List<ProductDto>> GetAllAsync();
    Task<List<ProductDto>> GetInventoryChecklistAsync(string? search = null, bool lowStockOnly = false) => throw new System.NotSupportedException();
    Task<ProductCatalogPage> GetPurchasePageAsync(int page, int size = 100, string? search = null, string? supplierId = null);
    Task<double> GetDefaultMinStockAsync() => Task.FromResult(5d);
    Task SetDefaultMinStockAsync(double value) => throw new System.NotSupportedException();
    // Implementations must page at the data source, not materialize GetAllAsync and slice in memory.
    Task<InventoryCatalogPage> GetInventoryPageAsync(int page, int size = 100, string? search = null, bool lowStockOnly = false);
    Task<ProductCatalogPage> GetPageAsync(int page, int size = 60, string? search = null, string? category = null);
    Task<List<ProductCategoryCount>> GetCategoryCountsAsync();
    Task<List<ProductDto>> GetByCategoryAsync(string category);
    Task<ProductDto?> GetByBarcodeAsync(string barcode);
    Task<ProductDto?> GetByIdAsync(string id);
    Task AddAsync(ProductDto product);
    Task UpdateAsync(ProductDto product);
    Task DeleteAsync(string id);
    Task ClearInventoryAsync();
    Task BulkSaveAsync(IEnumerable<ProductDto> products);
    Task<int> ImportFromCsvTextAsync(string csvContent);
    Task<List<ProductDto>> SearchFtsAsync(string query);
    Task<IEnumerable<ProductDto>> GetCatalogForPosAsync();
    Task<bool> AdjustStockManuallyAsync(string productId, double newPhysicalStock, string reason, string userId, Ticketfy.Services.Security.ISecurityInterceptionService? securityService = null, IAuditService? auditService = null);
}
