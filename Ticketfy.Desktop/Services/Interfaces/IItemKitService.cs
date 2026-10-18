using Ticketfy.Data.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public interface IItemKitService
{
    Task<List<ItemKitDto>> GetAllAsync();
    Task<ItemKitDto?> GetByBarcodeAsync(string barcode);
    Task<bool> SaveAsync(string id, string barcode, string name, decimal price, string description, List<ItemKitItemDto> items);
    Task<bool> DeleteAsync(string id);
    Task<bool> DeductKitStockAsync(string kitId, double kitQuantity);
}
