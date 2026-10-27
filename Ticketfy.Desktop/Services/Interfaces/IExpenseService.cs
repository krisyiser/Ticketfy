using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IExpenseService
{
    /// <summary>Compatibility read, limited to 200 documents. Use GetPageAsync for larger histories.</summary>
    Task<List<ExpenseDto>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null);
    Task<ExpensePageDto> GetPageAsync(int page, int size = 100, DateTime? startDate = null, DateTime? endDate = null,
        string? shiftId = null, bool activeShiftOnly = false, bool cancellationsOnly = false);
    Task<ExpenseDto> CreateAsync(ExpenseDto dto);
    Task<bool> DeleteAsync(string id);
    Task<bool> CancelAsync(string id, string reason, string operationId, bool reversalVerified = false) => throw new NotSupportedException();
    Task<FinancialSummaryDto> GetFinancialSummaryAsync(DateTime? startDate = null, DateTime? endDate = null, string? shiftId = null);
    Task<NetProfitReportModel> CalculateTrueNetProfitAsync(DateTime startDate, DateTime endDate);
}
