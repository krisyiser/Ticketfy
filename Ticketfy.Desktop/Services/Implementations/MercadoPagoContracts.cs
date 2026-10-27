using System.Text.Json.Serialization;

namespace Ticketfy.Services.Implementations;

public sealed class MercadoPagoOrderRequest
{
    [JsonPropertyName("type")] public string Type { get; set; } = "point";
    [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = "";
    [JsonPropertyName("expiration_time")] public string ExpirationTime { get; set; } = "PT3M";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("transactions")] public MercadoPagoTransactions Transactions { get; set; } = new();
    [JsonPropertyName("config")] public MercadoPagoConfig Config { get; set; } = new();
}
public sealed class MercadoPagoTransactions
{
    [JsonPropertyName("payments")] public MercadoPagoPayment[] Payments { get; set; } = [];
}
public sealed class MercadoPagoPayment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "";
    [JsonPropertyName("paid_amount")] public string? PaidAmount { get; set; }
    [JsonPropertyName("refunded_amount")] public string? RefundedAmount { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = "";
    [JsonPropertyName("authorization_code")] public string AuthorizationCode { get; set; } = "";
    [JsonPropertyName("payment_method")] public MercadoPagoPaymentMethod? PaymentMethod { get; set; }
}
public sealed class MercadoPagoPaymentMethod
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("card")] public MercadoPagoCard? Card { get; set; }
}
public sealed class MercadoPagoCard
{
    [JsonPropertyName("last_four_digits")] public string LastFourDigits { get; set; } = "";
}
public sealed class MercadoPagoConfig
{
    [JsonPropertyName("point")] public MercadoPagoPointConfig Point { get; set; } = new();
}
public sealed class MercadoPagoPointConfig
{
    [JsonPropertyName("terminal_id")] public string TerminalId { get; set; } = "";
    [JsonPropertyName("print_on_terminal")] public string PrintOnTerminal { get; set; } = "no_ticket";
}
public sealed class MercadoPagoOrderResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("country_code")] public string CountryCode { get; set; } = "";
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("external_reference")] public string ExternalReference { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = "";
    [JsonPropertyName("last_updated_date")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("config")] public MercadoPagoConfig? Config { get; set; }
    [JsonPropertyName("transactions")] public MercadoPagoTransactions? Transactions { get; set; }
}
[JsonSerializable(typeof(MercadoPagoOrderRequest))]
[JsonSerializable(typeof(MercadoPagoOrderResponse))]
public partial class MercadoPagoJsonContext : JsonSerializerContext;
