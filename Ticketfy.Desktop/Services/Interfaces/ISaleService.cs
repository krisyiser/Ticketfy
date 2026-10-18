using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public sealed record FiscalOutboxRequest(FacturamaCfdiRequest Request, string IdempotencyKey);

/// <summary>
/// Sale transactions with atomic stock deduction, debt increment, and cancellation rollback.
/// </summary>
public interface ISaleService
{
    event System.Action<SaleDto>? SaleSaved;
    Task<SaleDto> SaveAsync(SaleDto sale, FiscalOutboxRequest? fiscalOutbox = null);
    Task BeginCheckoutAsync(SaleDto sale);
    Task BeginCheckoutAsync(SaleDto sale, FiscalOutboxRequest? fiscalOutbox) => BeginCheckoutAsync(sale);
    Task MarkPaymentAuthorizedAsync(string operationId, string paymentReference);
    Task MarkCheckoutFailedAsync(string operationId, string reason, bool requiresReconciliation);
    Task<int> RecoverInterruptedCheckoutsAsync();
    Task ResolveInterruptedCheckoutAsync(string operationId, bool complete, bool fundsVerified, string reason) => throw new NotSupportedException();
    Task<List<PendingCheckoutDto>> GetInterruptedCheckoutsAsync() => throw new NotSupportedException();
    Task<SaleResultModel> ProcessSaleAsync(SaleCreationDto dto);
    Task<SaleDto?> GetByIdAsync(string saleId);
    Task<List<SaleDto>> GetHistoryAsync(int limit = 500);
    Task CancelAsync(string saleId);
    Task<bool> CancelSaleAsync(string saleId, string reason);
    Task<List<SaleDto>> GetSalesByDateRangeAsync(System.DateTime start, System.DateTime end);
    Task UpdateFiscalStatusAsync(string saleId, string status, string? uuid, string? folio);
    Task<bool> ProcessPartialReturnAsync(string saleId, string productId, double returnQty, string reason, string refundMethod = "Efectivo", bool isProductInGoodCondition = true, string? operationId = null);
    /// <summary>
    /// Computes aggregated cashier sales metrics and earned commissions within an optional date window, preventing un-bounded database memory dumps.
    /// </summary>
    Task<List<CashierPerformanceDto>> GetCashierPerformanceReportAsync(System.DateTime? startDate = null, System.DateTime? endDate = null, decimal defaultCommissionPct = 0m);
}

