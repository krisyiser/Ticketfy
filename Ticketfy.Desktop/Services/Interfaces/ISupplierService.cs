using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface ISupplierService
{
    /// <summary>Compatibility read, limited to 200 active suppliers. Larger catalogs require GetPageAsync.</summary>
    Task<List<SupplierDto>> GetAllAsync();
    Task<CatalogPage<SupplierDto>> GetPageAsync(int page, int size = 100, string? search = null);
    async Task<List<SupplierDto>> SearchAsync(string? search = null) => (await GetPageAsync(0, 100, search)).Items;
    Task<SupplierDto?> GetByIdAsync(string id);
    Task<SupplierDto> CreateAsync(SupplierDto dto);
    Task<SupplierDto> UpdateAsync(SupplierDto dto);
    Task<bool> DeleteAsync(string id);
}
