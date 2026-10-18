using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Models;
using Ticketfy.Data;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Facturama API Web adapter. Queueing and retry decisions intentionally live in
/// FiscalIntegrationQueueService so an HTTP retry can never duplicate a CFDI.
/// </summary>
public sealed class FacturamaService : IFacturamaService
{
    private readonly HttpClient _httpClient;
    private readonly SecureCredentialStore _credentials;

    public FacturamaService(HttpClient httpClient, SecureCredentialStore? credentials = null)
    {
        _httpClient = httpClient;
        _credentials = credentials ?? new SecureCredentialStore();
    }

    public async Task<FacturamaCfdiResponse?> CreateInvoiceAsync(
        FacturamaCfdiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!LocalDevelopmentMode.FacturamaEnabled)
            throw new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage);
        if (string.IsNullOrWhiteSpace(request.ExpeditionPlace) || request.ExpeditionPlace == "00000")
            request.ExpeditionPlace = await ReadRequiredIssuerZipCodeAsync(cancellationToken);
        using var message = await CreateRequestAsync(HttpMethod.Post, "3/cfdis", cancellationToken);
        message.Content = JsonContent.Create(request, FacturamaJsonContext.Default.FacturamaCfdiRequest);
        using var response = await SendAsync(message, deliveryCanBeUncertain: true, cancellationToken);
        return await response.Content.ReadFromJsonAsync(
            FacturamaJsonContext.Default.FacturamaCfdiResponse,
            cancellationToken);
    }

    public async Task<FacturamaCfdiResponse?> GetInvoiceAsync(
        string providerDocumentId,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerDocumentId);
        using var message = await CreateRequestAsync(
            HttpMethod.Get,
            $"cfdi/{Uri.EscapeDataString(providerDocumentId)}?type=issued",
            cancellationToken);
        using var response = await SendAsync(message, deliveryCanBeUncertain: false, cancellationToken);
        return await response.Content.ReadFromJsonAsync(
            FacturamaJsonContext.Default.FacturamaCfdiResponse,
            cancellationToken);
    }

    public async Task<FacturamaFileResponse> DownloadInvoiceAsync(
        string providerDocumentId,
        FacturamaFileFormat format,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerDocumentId);
        string value = format == FacturamaFileFormat.Xml ? "xml" : "pdf";
        using var message = await CreateRequestAsync(
            HttpMethod.Get,
            $"cfdi/{value}/issued/{Uri.EscapeDataString(providerDocumentId)}",
            cancellationToken);
        using var response = await SendAsync(message, deliveryCanBeUncertain: false, cancellationToken);
        return await response.Content.ReadFromJsonAsync(
                   FacturamaJsonContext.Default.FacturamaFileResponse,
                   cancellationToken)
               ?? throw new FacturamaProviderException("El proveedor devolvió un archivo vacío.");
    }

    public async Task<FacturamaCancellationResponse> CancelInvoiceAsync(
        string providerDocumentId,
        string motive,
        string? replacementUuid = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProviderId(providerDocumentId);
        if (motive is not ("01" or "02" or "03" or "04"))
            throw new ArgumentOutOfRangeException(nameof(motive), "El motivo SAT debe ser 01, 02, 03 o 04.");
        if (motive == "01" && string.IsNullOrWhiteSpace(replacementUuid))
            throw new ArgumentException("El motivo 01 exige el UUID de sustitución.", nameof(replacementUuid));

        string path = $"api/cfdi/{Uri.EscapeDataString(providerDocumentId)}?type=issued&motive={motive}";
        if (!string.IsNullOrWhiteSpace(replacementUuid))
            path += $"&uuidReplacement={Uri.EscapeDataString(replacementUuid)}";

        using var message = await CreateRequestAsync(HttpMethod.Delete, path, cancellationToken);
        using var response = await SendAsync(message, deliveryCanBeUncertain: true, cancellationToken);
        return await response.Content.ReadFromJsonAsync(
                   FacturamaJsonContext.Default.FacturamaCancellationResponse,
                   cancellationToken)
               ?? throw new FacturamaProviderException("El proveedor devolvió una cancelación vacía.");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        if (!LocalDevelopmentMode.FacturamaEnabled)
            throw new FacturamaUnavailableException(LocalDevelopmentMode.FacturamaUnavailableMessage);

        string user = _credentials.Read("FacturamaApiUser") ?? string.Empty;
        string password = _credentials.Read("FacturamaApiPassword") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            throw new FacturamaProviderException("Las credenciales fiscales no están configuradas.");

        string masterKey = SecurityManager.GetMasterKey();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={masterKey};")
            .Options;
        await using var context = new AppDbContext(options);
        string environment = (await context.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "FacturamaAmbiente", cancellationToken))?.Value ?? "Sandbox";
        var baseUri = new Uri(environment == "Producción"
            ? "https://api.facturama.mx/"
            : "https://apisandbox.facturama.mx/");

        var message = new HttpRequestMessage(method, new Uri(baseUri, path));
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        bool deliveryCanBeUncertain,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            HttpStatusCode code = response.StatusCode;
            bool transient = code == HttpStatusCode.RequestTimeout ||
                             code == HttpStatusCode.TooManyRequests ||
                             (int)code >= 500;
            response.Dispose();
            Log.Warning("Fiscal provider returned status {StatusCode}", (int)code);
            throw new FacturamaProviderException(
                "El proveedor fiscal rechazó temporal o permanentemente la operación.",
                code,
                transient,
                deliveryUncertain: deliveryCanBeUncertain && transient);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FacturamaProviderException(
                "El proveedor fiscal no respondió dentro del tiempo esperado.",
                isTransient: true,
                deliveryUncertain: deliveryCanBeUncertain);
        }
        catch (HttpRequestException ex)
        {
            throw new FacturamaProviderException(
                "No se pudo confirmar la respuesta del proveedor fiscal.",
                isTransient: true,
                deliveryUncertain: deliveryCanBeUncertain,
                innerException: ex);
        }
    }

    private static void ValidateProviderId(string providerDocumentId)
    {
        if (string.IsNullOrWhiteSpace(providerDocumentId) || providerDocumentId.Length > 128)
            throw new ArgumentException("Identificador fiscal inválido.", nameof(providerDocumentId));
    }

    private static async Task<string> ReadRequiredIssuerZipCodeAsync(CancellationToken cancellationToken)
    {
        string masterKey = SecurityManager.GetMasterKey();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={masterKey};")
            .Options;
        await using var context = new AppDbContext(options);
        string? zipCode = (await context.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "EmpresaCodigoPostalFiscal", cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(zipCode) || zipCode.Length != 5)
            throw new FacturamaProviderException("Configure el código postal fiscal del emisor antes de timbrar.");
        return zipCode;
    }
}
