using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IExpenseService
{
    Task<List<ExpenseDto>> GetAllAsync();
    Task<ExpenseDto> CreateAsync(ExpenseDto dto);
    Task<bool> DeleteAsync(string id);
    Task<bool> CancelAsync(string id, string reason, string operationId, bool reversalVerified = false) => throw new NotSupportedException();
    Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task<NetProfitReportModel> CalculateTrueNetProfitAsync(DateTime startDate, DateTime endDate);
}
