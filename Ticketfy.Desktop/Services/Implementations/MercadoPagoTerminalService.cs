using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public sealed class MercadoPagoOrderRequest
{
    [JsonPropertyName("type")] public string Type { get; set; } = "point";
    [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = string.Empty;
    [JsonPropertyName("expiration_time")] public string ExpirationTime { get; set; } = "PT3M";
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("transactions")] public MercadoPagoTransactions Transactions { get; set; } = new();
    [JsonPropertyName("config")] public MercadoPagoConfig Config { get; set; } = new();
}

public sealed class MercadoPagoTransactions
{
    [JsonPropertyName("payments")] public MercadoPagoPayment[] Payments { get; set; } = [];
}

public sealed class MercadoPagoPayment
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("amount")] public string Amount { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = string.Empty;
    [JsonPropertyName("authorization_code")] public string AuthorizationCode { get; set; } = string.Empty;
    [JsonPropertyName("payment_method")] public MercadoPagoPaymentMethod? PaymentMethod { get; set; }
}

public sealed class MercadoPagoPaymentMethod
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("card")] public MercadoPagoCard? Card { get; set; }
}

public sealed class MercadoPagoCard
{
    [JsonPropertyName("last_four_digits")] public string LastFourDigits { get; set; } = string.Empty;
}

public sealed class MercadoPagoConfig
{
    [JsonPropertyName("point")] public MercadoPagoPointConfig Point { get; set; } = new();
}

public sealed class MercadoPagoPointConfig
{
    [JsonPropertyName("terminal_id")] public string TerminalId { get; set; } = string.Empty;
    [JsonPropertyName("print_on_terminal")] public string PrintOnTerminal { get; set; } = "no_ticket";
}

public sealed class MercadoPagoOrderResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = string.Empty;
    [JsonPropertyName("transactions")] public MercadoPagoTransactions? Transactions { get; set; }
}

[JsonSerializable(typeof(MercadoPagoOrderRequest))]
[JsonSerializable(typeof(MercadoPagoOrderResponse))]
public partial class MercadoPagoJsonContext : JsonSerializerContext;

/// <summary>
/// Mercado Pago Point Orders API adapter. It persists the provider order ID
/// before polling, and every repeat uses the same external reference and
/// X-Idempotency-Key.
/// </summary>
public sealed class MercadoPagoTerminalService : IPaymentTerminalService
{
    private static readonly Regex ValidReference = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] PendingStatuses = ["created", "at_terminal", "processing", "action_required"];
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settings;
    private readonly SecureCredentialStore _credentials;
    private readonly IDbContextFactory<AppDbContext>? _contextFactory;
    private readonly Func<Task<string?>>? _accessTokenProvider;
    private readonly TimeSpan _pollingInterval;
    private readonly int _maxPollingAttempts;

    public MercadoPagoTerminalService(
        HttpClient httpClient,
        ISettingsService settings,
        SecureCredentialStore? credentials = null,
        IDbContextFactory<AppDbContext>? contextFactory = null,
        Func<Task<string?>>? accessTokenProvider = null,
        TimeSpan? pollingInterval = null,
        int maxPollingAttempts = 45)
    {
        _httpClient = httpClient;
        _settings = settings;
        _credentials = credentials ?? new SecureCredentialStore();
        _contextFactory = contextFactory;
        _accessTokenProvider = accessTokenProvider;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(2);
        _maxPollingAttempts = Math.Max(1, maxPollingAttempts);
        _httpClient.BaseAddress ??= new Uri("https://api.mercadopago.com");
    }

    public async Task<Result<PaymentResponse>> ProcessPaymentAsync(decimal amount, string referenceId, CancellationToken cancellationToken)
    {
        if (amount <= 0m) return Result<PaymentResponse>.Failure("El monto debe ser mayor a cero.");
        if (!ValidReference.IsMatch(referenceId))
            return Result<PaymentResponse>.Failure("La referencia de pago no cumple el formato permitido.");

        string? terminalId = await _settings.GetAsync("TerminalDeviceId");
        string? accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(accessToken))
            return Result<PaymentResponse>.Failure("La terminal no está configurada.");

        PaymentTerminalOperationEntity operation;
        try
        {
            operation = await GetOrCreateOperationAsync(referenceId, terminalId, amount, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Result<PaymentResponse>.Failure(ex.Message);
        }

        SetAuthorization(accessToken);
        MercadoPagoOrderResponse? order;
        try
        {
            if (string.IsNullOrWhiteSpace(operation.ProviderOrderId))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders");
                request.Headers.TryAddWithoutValidation("X-Idempotency-Key", operation.IdempotencyKey);
                request.Content = JsonContent.Create(new MercadoPagoOrderRequest
                {
                    ExternalReference = referenceId,
                    Description = $"Venta {referenceId}",
                    Transactions = new MercadoPagoTransactions
                    {
                        Payments = [new MercadoPagoPayment { Amount = Math.Round(amount, 2).ToString("0.00", CultureInfo.InvariantCulture) }]
                    },
                    Config = new MercadoPagoConfig { Point = new MercadoPagoPointConfig { TerminalId = terminalId } }
                }, MercadoPagoJsonContext.Default.MercadoPagoOrderRequest);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    bool uncertain = response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                                     response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                                     (int)response.StatusCode >= 500;
                    await RecordFailureAsync(
                        operation,
                        uncertain ? IntegrationOperationStatus.NeedsReconciliation : IntegrationOperationStatus.FailedPermanent,
                        ((int)response.StatusCode).ToString(),
                        uncertain ? "El proveedor no confirmó si creó la orden." : "El proveedor rechazó la orden.",
                        cancellationToken);
                    return Result<PaymentResponse>.Failure(uncertain
                        ? "No se pudo confirmar el cobro; reintente la misma operación para conciliar."
                        : "La terminal rechazó la solicitud de cobro.");
                }
                order = await response.Content.ReadFromJsonAsync(MercadoPagoJsonContext.Default.MercadoPagoOrderResponse, cancellationToken);
                if (order is null || string.IsNullOrWhiteSpace(order.Id))
                    throw new HttpRequestException("Respuesta de orden incompleta.");
                operation.ProviderOrderId = order.Id;
                operation.ProviderPaymentId = order.Transactions?.Payments.FirstOrDefault()?.Id;
                operation.ProviderStatus = order.Status;
                operation.Status = IntegrationOperationStatus.Processing;
                operation.AttemptCount++;
                operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                await SaveOperationAsync(operation, cancellationToken);
            }
            else
            {
                order = await GetOrderAsync(operation.ProviderOrderId, cancellationToken);
            }

            for (int attempt = 0; attempt < _maxPollingAttempts && IsPending(order?.Status); attempt++)
            {
                await Task.Delay(_pollingInterval, cancellationToken);
                order = await GetOrderAsync(operation.ProviderOrderId!, cancellationToken);
                await UpdateFromOrderAsync(operation, order, cancellationToken);
            }

            if (order is not null && string.Equals(order.Status, "processed", StringComparison.OrdinalIgnoreCase))
            {
                var payment = order.Transactions?.Payments.FirstOrDefault();
                operation.Status = IntegrationOperationStatus.Completed;
                operation.ProviderStatus = order.Status;
                operation.ProviderPaymentId = payment?.Id;
                operation.AuthorizationCode = payment?.AuthorizationCode;
                operation.CardBrand = payment?.PaymentMethod?.Id;
                operation.LastFourDigits = payment?.PaymentMethod?.Card?.LastFourDigits;
                operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                await SaveOperationAsync(operation, cancellationToken);
                return Result<PaymentResponse>.Success(new PaymentResponse
                {
                    TransactionId = payment?.Id ?? order.Id,
                    AuthorizationCode = payment?.AuthorizationCode ?? string.Empty,
                    CardBrand = payment?.PaymentMethod?.Id ?? string.Empty,
                    Last4Digits = payment?.PaymentMethod?.Card?.LastFourDigits ?? string.Empty
                });
            }

            string status = order?.Status ?? "unknown";
            operation.Status = IsPending(status)
                ? IntegrationOperationStatus.NeedsReconciliation
                : IntegrationOperationStatus.FailedPermanent;
            operation.ProviderStatus = status;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            await SaveOperationAsync(operation, cancellationToken);
            return Result<PaymentResponse>.Failure(IsPending(status)
                ? "El resultado del cobro no pudo confirmarse; requiere conciliación."
                : $"El cobro terminó con estado {status}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Status = IntegrationOperationStatus.NeedsReconciliation;
            operation.LastErrorCode = "client_cancelled";
            operation.LastErrorMessage = "Se interrumpió la espera; el cobro debe conciliarse.";
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            await SaveOperationAsync(operation, CancellationToken.None);
            return Result<PaymentResponse>.Failure("Operación interrumpida; se conciliará antes de repetir el cobro.");
        }
        catch (HttpRequestException)
        {
            operation.Status = IntegrationOperationStatus.NeedsReconciliation;
            operation.LastErrorCode = "network_uncertain";
            operation.LastErrorMessage = "No se pudo confirmar la respuesta de Mercado Pago.";
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            await SaveOperationAsync(operation, CancellationToken.None);
            return Result<PaymentResponse>.Failure("No se pudo confirmar el cobro; requiere conciliación.");
        }
    }

    public async Task<Result> CancelPaymentIntentAsync(string referenceId)
    {
        string? accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(accessToken)) return Result.Failure("La terminal no está configurada.");
        PaymentTerminalOperationEntity? operation = await FindOperationAsync(referenceId, CancellationToken.None);
        string? orderId = operation?.ProviderOrderId;
        if (string.IsNullOrWhiteSpace(orderId) && referenceId.StartsWith("ORD", StringComparison.Ordinal)) orderId = referenceId;
        if (string.IsNullOrWhiteSpace(orderId)) return Result.Failure("No existe una orden de proveedor para cancelar.");

        SetAuthorization(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(orderId)}/cancel");
        request.Headers.TryAddWithoutValidation(
            "X-Idempotency-Key",
            operation is null ? Guid.NewGuid().ToString() : operation.IdempotencyKey + "-cancel");
        request.Headers.TryAddWithoutValidation("x-allow-cancelable-status", "at_terminal");
        request.Content = JsonContent.Create(new { });
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return Result.Failure("Mercado Pago no aceptó la cancelación.");

        if (operation is not null)
        {
            operation.Status = response.StatusCode == System.Net.HttpStatusCode.Accepted
                ? IntegrationOperationStatus.NeedsReconciliation
                : IntegrationOperationStatus.Cancelled;
            operation.ProviderStatus = response.StatusCode == System.Net.HttpStatusCode.Accepted ? "cancellation_requested" : "canceled";
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            await SaveOperationAsync(operation, CancellationToken.None);
        }
        return Result.Success();
    }

    public async Task<bool> RequiresReconciliationAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        var operation = await FindOperationAsync(referenceId, cancellationToken);
        return operation?.Status == IntegrationOperationStatus.NeedsReconciliation;
    }

    private async Task<MercadoPagoOrderResponse?> GetOrderAsync(string orderId, CancellationToken cancellationToken) =>
        await _httpClient.GetFromJsonAsync(
            $"/v1/orders/{Uri.EscapeDataString(orderId)}",
            MercadoPagoJsonContext.Default.MercadoPagoOrderResponse,
            cancellationToken);

    private async Task<PaymentTerminalOperationEntity> GetOrCreateOperationAsync(
        string referenceId, string terminalId, decimal amount, CancellationToken cancellationToken)
    {
        var existing = await FindOperationAsync(referenceId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Amount != Math.Round(amount, 2) || existing.TerminalId != terminalId)
                throw new InvalidOperationException("La referencia ya existe con otro monto o terminal.");
            return existing;
        }

        string now = DateTimeOffset.UtcNow.ToString("O");
        var operation = new PaymentTerminalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ExternalReference = referenceId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            TerminalId = terminalId,
            Amount = Math.Round(amount, 2),
            Status = IntegrationOperationStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        try
        {
            await SaveOperationAsync(operation, cancellationToken, isNew: true);
            return operation;
        }
        catch (DbUpdateException)
        {
            existing = await FindOperationAsync(referenceId, cancellationToken);
            if (existing is null) throw;
            if (existing.Amount != Math.Round(amount, 2) || existing.TerminalId != terminalId)
                throw new InvalidOperationException("La referencia ya existe con otro monto o terminal.");
            return existing;
        }
    }

    private async Task<PaymentTerminalOperationEntity?> FindOperationAsync(string referenceId, CancellationToken cancellationToken)
    {
        if (_contextFactory is null) return null;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PaymentTerminalOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.ExternalReference == referenceId || o.ProviderOrderId == referenceId, cancellationToken);
    }

    private async Task SaveOperationAsync(PaymentTerminalOperationEntity operation, CancellationToken cancellationToken, bool isNew = false)
    {
        if (_contextFactory is null) return;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (isNew) context.PaymentTerminalOperations.Add(operation);
        else context.PaymentTerminalOperations.Update(operation);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateFromOrderAsync(PaymentTerminalOperationEntity operation, MercadoPagoOrderResponse? order, CancellationToken cancellationToken)
    {
        if (order is null) return;
        operation.ProviderStatus = order.Status;
        operation.ProviderPaymentId = order.Transactions?.Payments.FirstOrDefault()?.Id;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        await SaveOperationAsync(operation, cancellationToken);
    }

    private async Task RecordFailureAsync(
        PaymentTerminalOperationEntity operation,
        IntegrationOperationStatus status,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        operation.Status = status;
        operation.LastErrorCode = code;
        operation.LastErrorMessage = message;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        await SaveOperationAsync(operation, cancellationToken);
    }

    private void SetAuthorization(string accessToken) =>
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private async Task<string?> GetAccessTokenAsync()
    {
        if (_accessTokenProvider is not null) return await _accessTokenProvider();
        string? token = _credentials.Read("PaymentApiToken");
        if (!string.IsNullOrWhiteSpace(token)) return token;
        string? legacy = await _settings.GetAsync("PaymentApiToken");
        if (string.IsNullOrWhiteSpace(legacy)) return null;
        _credentials.Write("PaymentApiToken", legacy);
        await _settings.SetAsync("PaymentApiToken", string.Empty);
        return legacy;
    }

    private static bool IsPending(string? status) =>
        PendingStatuses.Contains(status ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}
