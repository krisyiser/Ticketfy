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
    Task<List<CustomerDto>> GetAllAsync();
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
