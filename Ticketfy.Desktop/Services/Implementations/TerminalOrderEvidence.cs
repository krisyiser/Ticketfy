using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal sealed record TerminalOrderEvidence(string OrderId, string PaymentId, string ProviderStatus,
    IntegrationOperationStatus Status, bool FundsObserved, DateTimeOffset UpdatedAt, string Hash,
    string? AuthorizationCode, string? CardBrand, string? LastFourDigits)
{
    internal const string Uncertain = "El estado bancario requiere conciliación. Conserve la referencia; no vuelva a cobrar.";
    internal static bool ValidId(string? value) => value is not null && Regex.IsMatch(value,
        "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    internal static bool NoFunds(PaymentTerminalOperationEntity value) => value.NoFundsConfirmed && !value.FundsObserved &&
        value.Status is IntegrationOperationStatus.Cancelled or IntegrationOperationStatus.FailedPermanent;
    internal static bool IsCaptured(PaymentTerminalOperationEntity? value) => value is
    { Status: IntegrationOperationStatus.Completed, FundsObserved: true, NoFundsConfirmed: false, ProviderEvidenceHash.Length: 64 } &&
        value.ProviderEvidenceHash.All(char.IsAsciiHexDigit) && !string.IsNullOrWhiteSpace(value.ProviderPaymentId) &&
        DateTimeOffset.TryParse(value.ProviderUpdatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    internal static TerminalOrderEvidence Parse(MercadoPagoOrderResponse? order, PaymentTerminalOperationEntity expected,
        string? requestedOrderId = null)
    {
        if (order is null || !ValidId(order.Id) || order.Type != "point" || order.CountryCode != "MEX" ||
            order.Currency is not (null or "MXN") ||
            order.ExternalReference != expected.ExternalReference ||
            order.Config?.Point?.TerminalId != expected.TerminalId ||
            (!string.IsNullOrEmpty(requestedOrderId) && order.Id != requestedOrderId) ||
            (!string.IsNullOrEmpty(expected.ProviderOrderId) && order.Id != expected.ProviderOrderId) ||
            order.Transactions?.Payments is not { Length: 1 })
            throw new InvalidDataException("La respuesta no corresponde a la orden, referencia o terminal originales.");
        var payment = order.Transactions.Payments[0];
        if (payment is null || !ValidId(payment.Id) || (!string.IsNullOrEmpty(expected.ProviderPaymentId) && expected.ProviderPaymentId != payment.Id)
            || !Money(payment.Amount, out var amount) || amount != expected.Amount ||
            !Money(payment.PaidAmount ?? "0", out var paid) || !Money(payment.RefundedAmount ?? "0", out var refunded) ||
            paid > amount || refunded > amount)
            throw new InvalidDataException("La evidencia bancaria no coincide con el pago e importe originales.");
        if (order.UpdatedAt is null || !Regex.IsMatch(order.UpdatedAt, @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$",
                RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(order.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            throw new InvalidDataException("La respuesta bancaria no tiene una fecha verificable.");

        // An unknown or refunded state is never proof of no funds; action_required
        // may remain unchanged at the provider and needs terminal/support evidence.
        bool funds = paid > 0 || refunded > 0 || order.Status is "processed" or "refunded" || payment.Status is "processed" or "refunded";
        var state = IntegrationOperationStatus.NeedsReconciliation;
        if (order.Status == "processed" && order.StatusDetail == "processed" && payment.Status == "processed" &&
            payment.StatusDetail == "accredited" && paid == amount && refunded == 0)
            state = IntegrationOperationStatus.Completed;
        else if (!funds && order.Status == payment.Status)
        {
            state = order.Status switch
            {
                "created" or "at_terminal" or "processing" => IntegrationOperationStatus.Processing,
                "canceled" when payment.StatusDetail is "canceled" or "canceled_by_api" or "canceled_on_terminal" => IntegrationOperationStatus.Cancelled,
                "expired" when payment.StatusDetail == "expired" => IntegrationOperationStatus.FailedPermanent,
                "failed" when KnownRejection(payment.StatusDetail) => IntegrationOperationStatus.FailedPermanent,
                _ => IntegrationOperationStatus.NeedsReconciliation
            };
        }
        string fingerprint = string.Join('\0', order.Id, payment.Id, amount.ToString("0.00", CultureInfo.InvariantCulture),
            paid.ToString("0.00", CultureInfo.InvariantCulture), refunded.ToString("0.00", CultureInfo.InvariantCulture),
            order.Status, order.StatusDetail, payment.Status, payment.StatusDetail);
        string providerStatus = order.Status is "created" or "at_terminal" or "processing" or "processed" or "action_required"
            or "canceled" or "expired" or "failed" or "refunded" ? order.Status : "unknown";
        return new(order.Id, payment.Id, providerStatus, state, funds, updated,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))),
            SafeLabel(payment.AuthorizationCode), SafeLabel(payment.PaymentMethod?.Id),
            payment.PaymentMethod?.Card?.LastFourDigits is { Length: 4 } digits && digits.All(char.IsAsciiDigit) ? digits : null);
    }
    private static bool Money(string value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) &&
        amount >= 0 && decimal.Round(amount, 2) == amount;
    private static string? SafeLabel(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;
    private static bool KnownRejection(string? detail) => detail is "failed" or "bad_filled_card_data" or "high_risk"
        or "rejected_by_issuer" or "required_call_for_authorize" or "max_attempts_exceeded" or "card_disabled"
        or "insufficient_amount" or "amount_limit_exceeded" or "processing_error" or "invalid_installments";
}
