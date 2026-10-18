using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Sale transaction entity. Items are stored as serialized JSON in ItemsJson.
/// Maps to legacy 'sales' SQLite table with fiscal timbrado columns.
/// </summary>
[Table("sales")]
public class SaleEntity
{
    public string? ShiftId { get; set; }
    public string? RegisterId { get; set; }
    public string? CashierUserId { get; set; }
    public string? CashierName { get; set; }
    public int LedgerVersion { get; set; }
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    [Column("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [Required]
    [Column("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary>JSON-serialized array of SaleItemSnapshot records.</summary>
    [Required]
    [Column("items")]
    public string ItemsJson { get; set; } = "[]";

    [Column("total")]
    public decimal Total { get; set; }

    [Column("totalCost")]
    public decimal TotalCost { get; set; }

    [Column("profit")]
    public decimal Profit { get; set; }

    [Column("paidAmount")]
    public decimal PaidAmount { get; set; }

    [Column("changeAmount")]
    public decimal ChangeAmount { get; set; }

    [Required]
    [Column("paymentMethod")]
    public string PaymentMethod { get; set; } = "Cash";

    [Column("cashAmount")]
    public decimal CashAmount { get; set; }

    [Column("cardAmount")]
    public decimal CardAmount { get; set; }

    [Column("giftcard_amount")]
    public decimal GiftcardAmount { get; set; }

    [Column("giftcard_number")]
    public string? GiftcardNumber { get; set; }

    [Column("customerId")]
    public string? CustomerId { get; set; }

    [Column("isCredit")]
    public int IsCredit { get; set; }

    [Column("isCancelled")]
    public int IsCancelled { get; set; }

    [Column("cancelledAt")]
    public string? CancelledAt { get; set; }

    [Column("invoice_id")]
    public string? InvoiceId { get; set; }

    [Column("invoice_status")]
    public string? InvoiceStatus { get; set; }

    [NotMapped]
    public bool IsInvoiced => !string.IsNullOrEmpty(InvoiceId);

    [Column("estado_fiscal")]
    public string EstadoFiscal { get; set; } = "PENDIENTE";

    [Column("uuid_sat")]
    public string? UuidSat { get; set; }

    [Column("serie_folio")]
    public string? SerieFolio { get; set; }

    [Column("status")]
    public SaleStatus Status { get; set; } = SaleStatus.Completed;

    [Column("cancellation_reason")]
    public string? CancellationReason { get; set; }

    [Column("cancellation_date")]
    public string? CancellationDate { get; set; }

    [Column("committed_at_utc")]
    public string? CommittedAtUtc { get; set; }
}
