using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

/// <summary>
/// Customer CRUD with debt tracking, payment history, active shift cash ledger injection, and loyalty points.
/// </summary>
public interface ICustomerService
{
    Task<AccountStatementDto> GetStatementAsync(string customerId) => throw new System.NotSupportedException();
    Task<AccountStatementPageDto> GetStatementPageAsync(string customerId, int page = 0, int size = 100, LedgerReadCut? cut = null) => throw new System.NotSupportedException();
    /// <summary>Compatibility read, limited to 200 active customers. Larger catalogs require GetPageAsync.</summary>
    Task<List<CustomerDto>> GetAllAsync();
    Task<CatalogPage<CustomerDto>> GetPageAsync(int page, int size = 100, string? search = null);
    async Task<List<CustomerDto>> SearchAsync(string? search = null) => (await GetPageAsync(0, 100, search)).Items;
    Task<CustomerDto?> GetByIdAsync(string id);
    Task AddAsync(CustomerDto customer);
    Task UpdateAsync(CustomerDto customer);
    Task UpdateDebtAsync(string customerId, decimal newDebt);
    Task AddPaymentAsync(CustomerPaymentDto payment);
    Task<bool> RegisterCustomerPaymentAsync(string customerId, decimal amount, string method, string notes, string? operationId = null);
    Task<List<CustomerPaymentDto>> GetPaymentsAsync(string customerId);
    Task DeleteAsync(string id);
    Task UpdatePointsAsync(string customerId, double points);
    Task<FiscalClientDto?> GetFiscalDataAsync(string customerId);
    Task SaveFiscalDataAsync(FiscalClientDto data);
}
