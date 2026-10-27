using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IPurchaseService
{
    /// <summary>Compatibility read, limited to 200 purchases. Larger histories require GetPageAsync.</summary>
    Task<List<PurchaseDto>> GetAllAsync();
    Task<List<PurchaseDto>> GetPageAsync(int page, int pageSize = 100, string? search = null);
    Task<PurchaseDto?> GetByIdAsync(string id);
    Task<PurchaseFinancialPage> GetFinancialHistoryAsync(string id, int page = 0, int size = 100) => throw new System.NotSupportedException();
    Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto);
    Task PayAsync(string purchaseId, decimal amount, string method, string? reference, string operationId) => throw new System.NotSupportedException();
    Task ReturnAsync(string purchaseItemId, decimal quantity, string reason, string operationId) => throw new System.NotSupportedException();
    Task<AccountStatementDto> GetStatementAsync(string supplierId) => throw new System.NotSupportedException();
    Task<AccountStatementPageDto> GetStatementPageAsync(string supplierId, int page = 0, int size = 100, LedgerReadCut? cut = null) => throw new System.NotSupportedException();
}
