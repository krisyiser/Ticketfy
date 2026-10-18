using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IPurchaseService
{
    Task<List<PurchaseDto>> GetAllAsync();
    Task<PurchaseDto?> GetByIdAsync(string id);
    Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto);
    Task PayAsync(string purchaseId, decimal amount, string method, string? reference, string operationId) => throw new System.NotSupportedException();
    Task ReturnAsync(string purchaseItemId, decimal quantity, string reason, string operationId) => throw new System.NotSupportedException();
    Task<AccountStatementDto> GetStatementAsync(string supplierId) => throw new System.NotSupportedException();
}
