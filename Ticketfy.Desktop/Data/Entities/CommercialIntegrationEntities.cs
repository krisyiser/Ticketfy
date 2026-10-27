using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

public enum FiscalOperationType
{
    Issue = 0,
    Cancel = 1,
    Download = 2,
    Reconcile = 3
}

public enum IntegrationOperationStatus
{
    Pending = 0,
    Processing = 1,
    WaitingRetry = 2,
    Completed = 3,
    NeedsReconciliation = 4,
    FailedPermanent = 5,
    Cancelled = 6
}

[Table("fiscal_operations")]
public sealed class FiscalOperationEntity
{
    [Key, Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required, Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, Column("sale_id")]
    public string SaleId { get; set; } = string.Empty;

    [Column("operation_type")]
    public FiscalOperationType OperationType { get; set; }

    [Column("status")]
    public IntegrationOperationStatus Status { get; set; }

    [Required, Column("payload_json")]
    public string PayloadJson { get; set; } = "{}";

    [Column("provider_document_id")]
    public string? ProviderDocumentId { get; set; }

    [Column("provider_status")]
    public string? ProviderStatus { get; set; }

    [Column("uuid_sat")]
    public string? UuidSat { get; set; }

    [Column("cancellation_motive")]
    public string? CancellationMotive { get; set; }

    [Column("replacement_uuid")]
    public string? ReplacementUuid { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("next_attempt_at_utc")]
    public string? NextAttemptAtUtc { get; set; }

    [Column("lease_until_utc")]
    public string? LeaseUntilUtc { get; set; }

    [Column("last_error_code")]
    public string? LastErrorCode { get; set; }

    [Column("last_error_message")]
    public string? LastErrorMessage { get; set; }

    [Column("xml_path")]
    public string? XmlPath { get; set; }

    [Column("xml_sha256")]
    public string? XmlSha256 { get; set; }

    [Column("pdf_path")]
    public string? PdfPath { get; set; }

    [Column("pdf_sha256")]
    public string? PdfSha256 { get; set; }

    [Required, Column("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [Required, Column("updated_at_utc")]
    public string UpdatedAtUtc { get; set; } = string.Empty;
}

[Table("payment_terminal_operations")]
public sealed class PaymentTerminalOperationEntity
{
    [Key, Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required, Column("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [Required, Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, Column("terminal_id")]
    public string TerminalId { get; set; } = string.Empty;

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("status")]
    public IntegrationOperationStatus Status { get; set; }

    [Column("provider_status")]
    public string? ProviderStatus { get; set; }

    [Column("provider_order_id")]
    public string? ProviderOrderId { get; set; }

    [Column("provider_payment_id")]
    public string? ProviderPaymentId { get; set; }

    [Column("authorization_code")]
    public string? AuthorizationCode { get; set; }

    [Column("card_brand")]
    public string? CardBrand { get; set; }

    [Column("last_four_digits")]
    public string? LastFourDigits { get; set; }

    [Column("funds_observed")]
    public bool FundsObserved { get; set; }

    [Column("no_funds_confirmed")]
    public bool NoFundsConfirmed { get; set; }

    [Column("provider_updated_at_utc")]
    public string? ProviderUpdatedAtUtc { get; set; }

    [Column("provider_evidence_hash")]
    public string? ProviderEvidenceHash { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("last_error_code")]
    public string? LastErrorCode { get; set; }

    [Column("last_error_message")]
    public string? LastErrorMessage { get; set; }

    [Required, Column("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [Required, Column("updated_at_utc")]
    public string UpdatedAtUtc { get; set; } = string.Empty;
}

[Table("integration_webhook_events")]
public sealed class IntegrationWebhookEventEntity
{
    [Key, Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required, Column("provider")]
    public string Provider { get; set; } = string.Empty;

    [Required, Column("event_id")]
    public string EventId { get; set; } = string.Empty;

    [Required, Column("resource_id")]
    public string ResourceId { get; set; } = string.Empty;

    [Required, Column("payload_sha256")]
    public string PayloadSha256 { get; set; } = string.Empty;

    [Column("processed")]
    public bool Processed { get; set; }

    [Required, Column("received_at_utc")]
    public string ReceivedAtUtc { get; set; } = string.Empty;

    [Column("processed_at_utc")]
    public string? ProcessedAtUtc { get; set; }
}
