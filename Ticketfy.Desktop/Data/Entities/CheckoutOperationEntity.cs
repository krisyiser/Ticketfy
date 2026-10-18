using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Durable checkout journal used to resume a locally committed sale after a
/// process or power failure. The operation id is also the idempotency key.
/// </summary>
[Table("checkout_operations")]
public sealed class CheckoutOperationEntity
{
    public string? ShiftId { get; set; }
    public string? RegisterId { get; set; }
    public string? CashierUserId { get; set; }
    public string? FiscalPayload { get; set; }
    public string? FiscalIdempotencyKey { get; set; }
    [Key]
    [Column("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [Column("sale_id")]
    public string? SaleId { get; set; }

    [Column("state")]
    public CheckoutOperationState State { get; set; } = CheckoutOperationState.Started;

    [Column("payment_method")]
    public string PaymentMethod { get; set; } = string.Empty;

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("payment_reference")]
    public string? PaymentReference { get; set; }

    [Required]
    [Column("sale_payload")]
    public string SalePayload { get; set; } = string.Empty;

    [Column("failure_reason")]
    public string? FailureReason { get; set; }

    [Column("created_at_utc")]
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [Column("updated_at_utc")]
    public string UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("o");
}
