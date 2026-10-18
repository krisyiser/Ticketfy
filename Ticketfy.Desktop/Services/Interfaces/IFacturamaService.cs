using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Models;

namespace Ticketfy.Services.Interfaces;

public interface IFacturamaService
{
    Task<FacturamaCfdiResponse?> CreateInvoiceAsync(FacturamaCfdiRequest request, CancellationToken cancellationToken = default);
    Task<FacturamaCfdiResponse?> GetInvoiceAsync(string providerDocumentId, CancellationToken cancellationToken = default);
    Task<FacturamaFileResponse> DownloadInvoiceAsync(string providerDocumentId, FacturamaFileFormat format, CancellationToken cancellationToken = default);
    Task<FacturamaCancellationResponse> CancelInvoiceAsync(string providerDocumentId, string motive, string? replacementUuid = null, CancellationToken cancellationToken = default);
}

public enum FacturamaFileFormat
{
    Xml,
    Pdf
}

public sealed class FacturamaProviderException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public bool IsTransient { get; }
    public bool DeliveryUncertain { get; }

    public FacturamaProviderException(
        string message,
        HttpStatusCode? statusCode = null,
        bool isTransient = false,
        bool deliveryUncertain = false,
        Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        DeliveryUncertain = deliveryUncertain;
    }
}
