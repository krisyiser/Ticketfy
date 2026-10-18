using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Interfaces;

public interface IFiscalIntegrationQueueService
{
    Task<FiscalOperationEntity> EnqueueIssueAsync(
        string saleId,
        FacturamaCfdiRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<FiscalOperationEntity> EnqueueCancellationAsync(
        string saleId,
        string providerDocumentId,
        string motive,
        string? replacementUuid,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<FiscalOperationEntity> EnqueueReconciliationAsync(
        string saleId,
        string providerDocumentId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default);
}
