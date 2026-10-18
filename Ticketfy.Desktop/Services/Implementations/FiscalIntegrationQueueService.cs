using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Models;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Durable fiscal outbox. A transport failure after sending an issue/cancel
/// request is never retried blindly: it is quarantined for reconciliation.
/// </summary>
public sealed class FiscalIntegrationQueueService : IFiscalIntegrationQueueService
{
    private const int MaxAttempts = 8;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IFacturamaService _provider;
    private readonly string _documentsRoot;
    private readonly Func<DateTimeOffset> _utcNow;

    public FiscalIntegrationQueueService(
        IDbContextFactory<AppDbContext> contextFactory,
        IFacturamaService provider,
        string? documentsRoot = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _contextFactory = contextFactory;
        _provider = provider;
        _documentsRoot = documentsRoot ?? AppDataPaths.FiscalDocumentsDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FiscalOperationEntity> EnqueueIssueAsync(
        string saleId,
        FacturamaCfdiRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(saleId, idempotencyKey);
        request.OrderNumber ??= idempotencyKey[..Math.Min(idempotencyKey.Length, 100)];
        request.Folio ??= NormalizeFolio(saleId);
        string now = _utcNow().ToString("O");
        var operation = new FiscalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SaleId = saleId,
            IdempotencyKey = idempotencyKey,
            OperationType = FiscalOperationType.Issue,
            Status = IntegrationOperationStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(request, FacturamaJsonContext.Default.FacturamaCfdiRequest),
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        return await InsertOrGetAsync(operation, cancellationToken);
    }

    public async Task<FiscalOperationEntity> EnqueueCancellationAsync(
        string saleId,
        string providerDocumentId,
        string motive,
        string? replacementUuid,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(saleId, idempotencyKey);
        if (string.IsNullOrWhiteSpace(providerDocumentId))
            throw new ArgumentException("Se requiere el identificador del CFDI.", nameof(providerDocumentId));
        if (motive is not ("01" or "02" or "03" or "04"))
            throw new ArgumentOutOfRangeException(nameof(motive));
        if (motive == "01" && string.IsNullOrWhiteSpace(replacementUuid))
            throw new ArgumentException("El motivo 01 exige UUID de sustitución.", nameof(replacementUuid));

        string now = _utcNow().ToString("O");
        var operation = new FiscalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SaleId = saleId,
            IdempotencyKey = idempotencyKey,
            OperationType = FiscalOperationType.Cancel,
            Status = IntegrationOperationStatus.Pending,
            PayloadJson = "{}",
            ProviderDocumentId = providerDocumentId,
            CancellationMotive = motive,
            ReplacementUuid = replacementUuid,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        return await InsertOrGetAsync(operation, cancellationToken);
    }

    public async Task<FiscalOperationEntity> EnqueueReconciliationAsync(
        string saleId,
        string providerDocumentId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(saleId, idempotencyKey);
        if (string.IsNullOrWhiteSpace(providerDocumentId))
            throw new ArgumentException("Se requiere el identificador del CFDI.", nameof(providerDocumentId));
        string now = _utcNow().ToString("O");
        return await InsertOrGetAsync(new FiscalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SaleId = saleId,
            IdempotencyKey = idempotencyKey,
            OperationType = FiscalOperationType.Reconcile,
            Status = IntegrationOperationStatus.Pending,
            PayloadJson = "{}",
            ProviderDocumentId = providerDocumentId,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        }, cancellationToken);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        string now = _utcNow().ToString("O");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var candidate = await context.FiscalOperations.AsNoTracking()
            .Where(o =>
                ((o.Status == IntegrationOperationStatus.Pending || o.Status == IntegrationOperationStatus.WaitingRetry) &&
                 (o.NextAttemptAtUtc == null || string.Compare(o.NextAttemptAtUtc, now) <= 0)) ||
                (o.Status == IntegrationOperationStatus.Processing && o.LeaseUntilUtc != null && string.Compare(o.LeaseUntilUtc, now) <= 0))
            .OrderBy(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;

        string lease = _utcNow().AddMinutes(2).ToString("O");
        int claimed = await context.FiscalOperations
            .Where(o => o.Id == candidate.Id &&
                        o.Status == candidate.Status &&
                        o.LeaseUntilUtc == candidate.LeaseUntilUtc &&
                        o.UpdatedAtUtc == candidate.UpdatedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, IntegrationOperationStatus.Processing)
                .SetProperty(o => o.LeaseUntilUtc, lease)
                .SetProperty(o => o.UpdatedAtUtc, now), cancellationToken);
        if (claimed != 1) return true;

        try
        {
            await ProcessClaimedAsync(candidate.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ScheduleRetryAsync(candidate.Id, "cancelled", "Procesamiento interrumpido por apagado.", cancellationToken: default);
            throw;
        }
        return true;
    }

    private async Task ProcessClaimedAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await context.FiscalOperations.SingleAsync(o => o.Id == operationId, cancellationToken);
        operation.AttemptCount++;
        operation.UpdatedAtUtc = _utcNow().ToString("O");
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            switch (operation.OperationType)
            {
                case FiscalOperationType.Issue:
                    await IssueAndDownloadAsync(context, operation, cancellationToken);
                    break;
                case FiscalOperationType.Cancel:
                    await CancelAsync(context, operation, cancellationToken);
                    break;
                case FiscalOperationType.Download:
                case FiscalOperationType.Reconcile:
                    await ReconcileAndDownloadAsync(context, operation, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException("Tipo de operación fiscal no soportado.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Let ProcessNextAsync release the lease and schedule a safe retry.
            // Treating shutdown cancellation as a permanent provider failure would
            // strand an otherwise recoverable fiscal operation.
            throw;
        }
        catch (FacturamaUnavailableException ex)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.WaitingRetry, "provider_paused", ex.Message, cancellationToken);
        }
        catch (FacturamaProviderException ex) when (ex.DeliveryUncertain)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.NeedsReconciliation, "delivery_uncertain", ex.Message, cancellationToken);
        }
        catch (FacturamaProviderException ex) when (ex.IsTransient && operation.AttemptCount < MaxAttempts)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.WaitingRetry, ((int?)ex.StatusCode)?.ToString() ?? "transient", ex.Message, cancellationToken);
        }
        catch (IOException ex) when (operation.AttemptCount < MaxAttempts)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.WaitingRetry, "local_io", Sanitize(ex.Message), cancellationToken);
        }
        catch (UnauthorizedAccessException ex) when (operation.AttemptCount < MaxAttempts)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.WaitingRetry, "local_access", Sanitize(ex.Message), cancellationToken);
        }
        catch (Exception ex)
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.FailedPermanent, "permanent", Sanitize(ex.Message), cancellationToken);
        }
    }

    private async Task IssueAndDownloadAsync(AppDbContext context, FiscalOperationEntity operation, CancellationToken cancellationToken)
    {
        FacturamaCfdiResponse? response;
        if (string.IsNullOrWhiteSpace(operation.ProviderDocumentId))
        {
            var request = JsonSerializer.Deserialize(operation.PayloadJson, FacturamaJsonContext.Default.FacturamaCfdiRequest)
                          ?? throw new InvalidOperationException("Payload fiscal inválido.");
            response = await _provider.CreateInvoiceAsync(request, cancellationToken);
        }
        else
        {
            response = await _provider.GetInvoiceAsync(operation.ProviderDocumentId, cancellationToken);
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Id))
            throw new FacturamaProviderException("El proveedor no devolvió un identificador fiscal.", deliveryUncertain: true);

        operation.ProviderDocumentId = response.Id;
        operation.ProviderStatus = response.Status;
        operation.UuidSat = response.SatUuid;
        await context.SaveChangesAsync(cancellationToken);
        await DownloadFilesAsync(operation, cancellationToken);
        await CompleteAndUpdateSaleAsync(context, operation, "TIMBRADO CFDI 4.0", cancellationToken);
    }

    private async Task CancelAsync(AppDbContext context, FiscalOperationEntity operation, CancellationToken cancellationToken)
    {
        var response = await _provider.CancelInvoiceAsync(
            operation.ProviderDocumentId!, operation.CancellationMotive!, operation.ReplacementUuid, cancellationToken);
        operation.ProviderStatus = response.Status;
        operation.UuidSat ??= response.Uuid;

        if (string.Equals(response.Status, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            await SetFailureAsync(context, operation, IntegrationOperationStatus.FailedPermanent, "cancel_rejected", "El SAT rechazó la cancelación.", cancellationToken);
            return;
        }

        string saleState = string.Equals(response.Status, "canceled", StringComparison.OrdinalIgnoreCase)
            ? "CANCELADO"
            : "CANCELACION SOLICITADA";
        await CompleteAndUpdateSaleAsync(context, operation, saleState, cancellationToken);
    }

    private async Task ReconcileAndDownloadAsync(AppDbContext context, FiscalOperationEntity operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.ProviderDocumentId))
            throw new InvalidOperationException("La conciliación requiere el identificador de proveedor.");
        var response = await _provider.GetInvoiceAsync(operation.ProviderDocumentId, cancellationToken)
                       ?? throw new FacturamaProviderException("No se encontró el CFDI para conciliar.");
        operation.ProviderStatus = response.Status;
        operation.UuidSat = response.SatUuid;
        await DownloadFilesAsync(operation, cancellationToken);
        await CompleteAndUpdateSaleAsync(context, operation, response.IsActive ? "TIMBRADO CFDI 4.0" : "CANCELADO", cancellationToken);
    }

    private async Task DownloadFilesAsync(FiscalOperationEntity operation, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_documentsRoot);
        string directory = Path.Combine(_documentsRoot, SafeSegment(operation.SaleId));
        Directory.CreateDirectory(directory);

        var xml = await _provider.DownloadInvoiceAsync(operation.ProviderDocumentId!, FacturamaFileFormat.Xml, cancellationToken);
        var pdf = await _provider.DownloadInvoiceAsync(operation.ProviderDocumentId!, FacturamaFileFormat.Pdf, cancellationToken);
        (operation.XmlPath, operation.XmlSha256) = await PersistFileAsync(directory, "cfdi.xml", xml, cancellationToken);
        (operation.PdfPath, operation.PdfSha256) = await PersistFileAsync(directory, "cfdi.pdf", pdf, cancellationToken);
    }

    private static async Task<(string path, string sha256)> PersistFileAsync(
        string directory,
        string fileName,
        FacturamaFileResponse response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(response.ContentEncoding, "base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codificación de documento fiscal no soportada.");
        if (response.ContentLength is < 0 or > 25_000_000 || response.Content.Length > 34_000_000)
            throw new InvalidDataException("El documento fiscal excede el tamaño permitido.");
        byte[] bytes = Convert.FromBase64String(response.Content);
        if (response.ContentLength > 0 && response.ContentLength != bytes.LongLength)
            throw new InvalidDataException("El tamaño del documento fiscal no coincide.");
        string destination = Path.Combine(directory, fileName);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
        return (destination, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private async Task CompleteAndUpdateSaleAsync(
        AppDbContext context,
        FiscalOperationEntity operation,
        string fiscalState,
        CancellationToken cancellationToken)
    {
        operation.Status = IntegrationOperationStatus.Completed;
        operation.LeaseUntilUtc = null;
        operation.NextAttemptAtUtc = null;
        operation.LastErrorCode = null;
        operation.LastErrorMessage = null;
        operation.UpdatedAtUtc = _utcNow().ToString("O");
        var sale = await context.Sales.FirstOrDefaultAsync(s => s.Id == operation.SaleId, cancellationToken);
        if (sale is not null)
        {
            sale.InvoiceId = operation.ProviderDocumentId;
            sale.InvoiceStatus = operation.ProviderStatus;
            sale.UuidSat = operation.UuidSat;
            sale.EstadoFiscal = fiscalState;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task SetFailureAsync(
        AppDbContext context,
        FiscalOperationEntity operation,
        IntegrationOperationStatus status,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        operation.Status = status;
        operation.LeaseUntilUtc = null;
        operation.LastErrorCode = code;
        operation.LastErrorMessage = Sanitize(message);
        operation.UpdatedAtUtc = _utcNow().ToString("O");
        operation.NextAttemptAtUtc = status == IntegrationOperationStatus.WaitingRetry
            ? _utcNow().Add(Backoff(operation.AttemptCount)).ToString("O")
            : null;
        await context.SaveChangesAsync(cancellationToken);
        Log.Warning("Fiscal operation {OperationId} moved to {Status} with code {Code}", operation.Id, status, code);
    }

    private async Task ScheduleRetryAsync(string operationId, string code, string message, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await context.FiscalOperations.SingleAsync(o => o.Id == operationId, cancellationToken);
        await SetFailureAsync(context, operation, IntegrationOperationStatus.WaitingRetry, code, message, cancellationToken);
    }

    private async Task<FiscalOperationEntity> InsertOrGetAsync(FiscalOperationEntity operation, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.FiscalOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.IdempotencyKey == operation.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.SaleId != operation.SaleId || existing.OperationType != operation.OperationType)
                throw new InvalidOperationException("La clave de idempotencia ya pertenece a otra operación fiscal.");
            return existing;
        }
        context.FiscalOperations.Add(operation);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException)
        {
            context.Entry(operation).State = EntityState.Detached;
            return await context.FiscalOperations.AsNoTracking()
                       .SingleAsync(o => o.IdempotencyKey == operation.IdempotencyKey, cancellationToken);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));
    private static string NormalizeFolio(string saleId) => new(saleId.Where(char.IsLetterOrDigit).Take(40).ToArray());
    private static string SafeSegment(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray());
    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(value.Length, 300)];

    private static void ValidateIdentity(string saleId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(saleId)) throw new ArgumentException("Venta requerida.", nameof(saleId));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ArgumentException("Clave de idempotencia inválida.", nameof(idempotencyKey));
    }
}
