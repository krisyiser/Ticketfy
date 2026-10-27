using Ticketfy.Data.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public interface IItemKitService
{
    Task<List<ItemKitDto>> GetAllAsync();
    Task<CatalogPage<KitDirectoryRow>> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false);
    Task<KitComponentPage?> GetComponentsPageAsync(string id, int page, int size = 100);
    Task<ItemKitDto?> GetByBarcodeAsync(string barcode);
    Task<KitCapabilities> GetCapabilitiesAsync();
    Task<KitDraft?> GetForEditAsync(string productId);
    Task SaveAsync(KitDraft draft, string operationId);
    Task SetActiveAsync(string productId, bool active, string operationId);
    Task<bool> DeductKitStockAsync(string kitId, double kitQuantity);
}
