using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

/// <summary>Creates a charge once. All subsequent reconciliation uses GET on a bound provider order.</summary>
public sealed partial class MercadoPagoTerminalService : IPaymentTerminalService, IPaymentTerminalReconciliation
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settings;
    private readonly SecureCredentialStore _credentials;
    private readonly IDbContextFactory<AppDbContext>? _contextFactory;
    private readonly Func<Task<string?>>? _accessTokenProvider;
    private readonly TimeSpan _pollingInterval;
    private readonly int _maxPollingAttempts;
    private readonly MaintenanceHealth? _maintenance;
    private readonly CommercialOperationContext? _operations;

    public MercadoPagoTerminalService(HttpClient httpClient, ISettingsService settings, SecureCredentialStore? credentials = null,
        IDbContextFactory<AppDbContext>? contextFactory = null, Func<Task<string?>>? accessTokenProvider = null,
        TimeSpan? pollingInterval = null, int maxPollingAttempts = 45, MaintenanceHealth? maintenance = null,
        CommercialOperationContext? operations = null)
    {
        _httpClient = httpClient; _settings = settings; _credentials = credentials ?? new SecureCredentialStore();
        _contextFactory = contextFactory; _accessTokenProvider = accessTokenProvider;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(2); _maxPollingAttempts = Math.Max(1, maxPollingAttempts);
        _maintenance = maintenance; _operations = operations;
        _httpClient.BaseAddress ??= new Uri("https://api.mercadopago.com");
    }

    public async Task<Result<PaymentResponse>> ProcessPaymentAsync(decimal amount, string referenceId, CancellationToken cancellationToken)
    {
        PaymentTerminalOperationEntity? operation = null;
        try
        {
            RequirePersistence();
            if (!TerminalOrderEvidence.ValidId(referenceId) || amount <= 0 || CommercialLedger.Money(amount) != amount)
                return Result<PaymentResponse>.Failure("Indique referencia e importe positivo con hasta dos decimales.");
            await RequireCheckoutAsync(referenceId, amount, recovery: false);
            string? terminalId = await _settings.GetAsync("TerminalDeviceId");
            string token = await RequireTokenAsync();
            _maintenance?.RequireWritesAllowed();
            if (string.IsNullOrWhiteSpace(terminalId)) return Result<PaymentResponse>.Failure("La terminal no está configurada.");
            bool created;
            (operation, created) = await GetOrCreateAsync(referenceId, terminalId, amount, cancellationToken);
            if (!created)
            {
                if (TerminalOrderEvidence.IsCaptured(operation))
                    return PaymentResult(operation);
                // Do not replay creation after an ambiguous response, even with the same key.
                if (string.IsNullOrEmpty(operation.ProviderOrderId) || TerminalOrderEvidence.NoFunds(operation))
                    return Result<PaymentResponse>.Failure(TerminalOrderEvidence.Uncertain);
            }
            MercadoPagoOrderResponse? order;
            if (created)
            {
                using var request = Request(HttpMethod.Post, "/v1/orders", token);
                request.Headers.TryAddWithoutValidation("X-Idempotency-Key", operation.IdempotencyKey);
                request.Content = JsonContent.Create(new MercadoPagoOrderRequest
                {
                    ExternalReference = referenceId,
                    Description = $"Venta {referenceId}",
                    Transactions = new() { Payments = [new() { Amount = amount.ToString("0.00", CultureInfo.InvariantCulture) }] },
                    Config = new() { Point = new() { TerminalId = terminalId } }
                }, MercadoPagoJsonContext.Default.MercadoPagoOrderRequest);
                await RequireCheckoutAsync(referenceId, amount, recovery: false);
                _maintenance?.RequireWritesAllowed();
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    bool rejected = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;
                    await RecordTransportFailureAsync(operation.Id, rejected, ((int)response.StatusCode).ToString());
                    return Result<PaymentResponse>.Failure(rejected
                        ? "El proveedor rechazó crear la orden; verifique la configuración antes de un nuevo intento."
                        : TerminalOrderEvidence.Uncertain);
                }
                order = await ReadOrderAsync(response, cancellationToken);
            }
            else order = await GetOrderAsync(operation.ProviderOrderId!, token, cancellationToken);

            operation = await ApplyOrderAsync(operation, order, recovery: false, requestedOrderId: operation.ProviderOrderId, cancellationToken);
            for (int attempt = 0; attempt < _maxPollingAttempts && operation.Status == IntegrationOperationStatus.Processing; attempt++)
            {
                await Task.Delay(_pollingInterval, cancellationToken);
                order = await GetOrderAsync(operation.ProviderOrderId!, token, cancellationToken);
                operation = await ApplyOrderAsync(operation, order, recovery: false, operation.ProviderOrderId, cancellationToken);
            }
            return PaymentResult(operation);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            if (operation is not null) await RecordTransportFailureAsync(operation.Id, false, "response_unverified");
            return Result<PaymentResponse>.Failure(SafeError(ex));
        }
    }

    public async Task<Result<TerminalReconciliationResult>> RefreshPaymentAsync(string referenceId, string? providerOrderId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            RequirePersistence();
            var operation = await FindOperationAsync(referenceId, cancellationToken)
                ?? throw new InvalidOperationException("No existe el intento bancario original. No se creará otro.");
            var approved = await RequireCheckoutAsync(referenceId, operation.Amount, recovery: true);
            string orderId = operation.ProviderOrderId ?? providerOrderId?.Trim() ?? "";
            if (!TerminalOrderEvidence.ValidId(orderId) || (!string.IsNullOrWhiteSpace(providerOrderId) && orderId != providerOrderId.Trim()))
                throw new InvalidOperationException("Indique el ID de la orden original obtenido del proveedor, no la referencia local.");
            string token = await RequireTokenAsync();
            if ((await RequireCheckoutAsync(referenceId, operation.Amount, recovery: true)).UserId != approved.UserId)
                throw new UnauthorizedAccessException("La sesión cambió durante la consulta.");
            var order = await GetOrderAsync(orderId, token, cancellationToken);
            operation = await ApplyOrderAsync(operation, order, recovery: true, orderId, cancellationToken, approved.UserId);
            return Result<TerminalReconciliationResult>.Success(new(referenceId, operation.ProviderOrderId!, operation.Status.ToString(),
                TerminalOrderEvidence.IsCaptured(operation), TerminalOrderEvidence.NoFunds(operation)));
        }
        catch (Exception ex) when (Recoverable(ex))
        { return Result<TerminalReconciliationResult>.Failure(SafeError(ex)); }
    }

    public async Task<Result> CancelPaymentIntentAsync(string referenceId)
    {
        try
        {
            RequirePersistence();
            var operation = await FindOperationAsync(referenceId, default)
                ?? throw new InvalidOperationException("No existe el intento bancario original.");
            var approved = await RequireCheckoutAsync(referenceId, operation.Amount, recovery: true);
            if (!TerminalOrderEvidence.ValidId(operation.ProviderOrderId) || operation.FundsObserved ||
                operation.Status is IntegrationOperationStatus.Completed or IntegrationOperationStatus.Cancelled or IntegrationOperationStatus.FailedPermanent)
                throw new InvalidOperationException("No se puede cancelar este intento; consulte su orden original.");
            string token = await RequireTokenAsync();
            if ((await RequireCheckoutAsync(referenceId, operation.Amount, recovery: true)).UserId != approved.UserId)
                throw new UnauthorizedAccessException("La sesión cambió durante la cancelación.");
            operation = await FindOperationAsync(referenceId, default)
                ?? throw new InvalidOperationException("No existe el intento bancario original.");
            if (operation.FundsObserved || operation.Status is IntegrationOperationStatus.Completed
                or IntegrationOperationStatus.Cancelled or IntegrationOperationStatus.FailedPermanent)
                return Result.Failure(TerminalOrderEvidence.Uncertain);
            using var request = Request(HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(operation.ProviderOrderId!)}/cancel", token);
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key", operation.IdempotencyKey + "-cancel");
            request.Headers.TryAddWithoutValidation("x-allow-cancelable-status", "at_terminal");
            await RequireCheckoutAsync(referenceId, operation.Amount, recovery: true);
            _maintenance?.RequireWritesAllowed();
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return Result.Failure("No se confirmó la cancelación; consulte la orden original.");
            // 202 is only an acknowledgement, never proof of cancellation. Query exact order.
            var order = await GetOrderAsync(operation.ProviderOrderId!, token, default);
            operation = await ApplyOrderAsync(operation, order, recovery: true, operation.ProviderOrderId, default, approved.UserId);
            return TerminalOrderEvidence.NoFunds(operation) ? Result.Success() : Result.Failure(TerminalOrderEvidence.Uncertain);
        }
        catch (Exception ex) when (Recoverable(ex)) { return Result.Failure(SafeError(ex)); }
    }

    public async Task<bool> RequiresReconciliationAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        var operation = await FindOperationAsync(referenceId, cancellationToken);
        return operation is not null && !TerminalOrderEvidence.NoFunds(operation);
    }
    private static Result<PaymentResponse> PaymentResult(PaymentTerminalOperationEntity operation) =>
        TerminalOrderEvidence.IsCaptured(operation)
            ? Result<PaymentResponse>.Success(new()
            {
                TransactionId = operation.ProviderPaymentId!,
                AuthorizationCode = operation.AuthorizationCode ?? "",
                CardBrand = operation.CardBrand ?? "",
                Last4Digits = operation.LastFourDigits ?? ""
            }) : Result<PaymentResponse>.Failure(TerminalOrderEvidence.NoFunds(operation)
                ? "La orden bancaria terminó sin pago confirmado." : TerminalOrderEvidence.Uncertain);
    private void RequirePersistence()
    {
        _maintenance?.RequireWritesAllowed();
        if (_contextFactory is null || _operations is null)
            throw new UnauthorizedAccessException("Se requiere persistencia y sesión autenticada para operar la terminal.");
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private async Task<MercadoPagoOrderResponse?> GetOrderAsync(string orderId, string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(orderId)}", token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadOrderAsync(response, cancellationToken);
    }
    private static async Task<MercadoPagoOrderResponse?> ReadOrderAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(131072, cancellationToken);
        return await response.Content.ReadFromJsonAsync(MercadoPagoJsonContext.Default.MercadoPagoOrderResponse, cancellationToken);
    }
    private async Task<string> RequireTokenAsync()
    {
        string? token;
        if (_accessTokenProvider is not null) token = await _accessTokenProvider();
        else
        {
            token = _credentials.Read("PaymentApiToken");
            if (string.IsNullOrWhiteSpace(token))
            {
                token = await _settings.GetAsync("PaymentApiToken");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _credentials.Write("PaymentApiToken", token);
                    await _settings.SetAsync("PaymentApiToken", "");
                }
            }
        }
        return !string.IsNullOrWhiteSpace(token) ? token : throw new InvalidOperationException("La terminal no está configurada.");
    }
    private static bool Recoverable(Exception ex) => ex is InvalidOperationException or UnauthorizedAccessException
        or IOException or InvalidDataException or HttpRequestException or JsonException or OperationCanceledException
        or DbUpdateException or System.Data.Common.DbException;
    private static string SafeError(Exception ex) => ex switch
    {
        MaintenanceWriteBlockedException => MaintenanceHealth.WriteBlockedMessage,
        UnauthorizedAccessException => "No está autorizado para consultar u operar este cobro.",
        _ => TerminalOrderEvidence.Uncertain
    };
}
