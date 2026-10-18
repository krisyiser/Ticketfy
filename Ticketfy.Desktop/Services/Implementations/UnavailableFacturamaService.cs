using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Models;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Local-development implementation. It guarantees that requesting an invoice
/// never opens a connection to Facturama while that integration is out of scope.
/// </summary>
public sealed class UnavailableFacturamaService : IFacturamaService
{
    public Task<FacturamaCfdiResponse?> CreateInvoiceAsync(FacturamaCfdiRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromException<FacturamaCfdiResponse?>(
            new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage));
    }

    public Task<FacturamaCfdiResponse?> GetInvoiceAsync(string providerDocumentId, CancellationToken cancellationToken = default) =>
        Task.FromException<FacturamaCfdiResponse?>(new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage));

    public Task<FacturamaFileResponse> DownloadInvoiceAsync(string providerDocumentId, FacturamaFileFormat format, CancellationToken cancellationToken = default) =>
        Task.FromException<FacturamaFileResponse>(new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage));

    public Task<FacturamaCancellationResponse> CancelInvoiceAsync(string providerDocumentId, string motive, string? replacementUuid = null, CancellationToken cancellationToken = default) =>
        Task.FromException<FacturamaCancellationResponse>(new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage));
}

public sealed class FacturamaUnavailableException(string message) : InvalidOperationException(message);
