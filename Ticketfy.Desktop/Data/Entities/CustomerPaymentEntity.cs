using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Payment record against a customer's outstanding debt.
/// Binds to the active shift for cash auditing.
/// Maps to legacy 'customer_payments' SQLite table.
/// </summary>
[Table("customer_payments")]
public class CustomerPaymentEntity
{
    public string? RegisterId { get; set; }
    public string? CashierUserId { get; set; }
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("customerId")]
    public string CustomerId { get; set; } = string.Empty;

    [Column("shift_id")]
    public string? ShiftId { get; set; }

    [Required]
    [Column("date")]
    public string Date { get; set; } = DateTime.Now.ToString("s");

    [Column("amount")]
    public decimal Amount { get; set; }

    [Required]
    [Column("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [Column("status")]
    public Ticketfy.Core.Enums.PaymentStatus Status { get; set; } = Ticketfy.Core.Enums.PaymentStatus.Completed;

    [Column("payment_method")]
    public string Method { get; set; } = "Efectivo";

    [Column("notes")]
    public string Notes { get; set; } = string.Empty;

    /// <summary>Navigation: parent customer.</summary>
    [ForeignKey(nameof(CustomerId))]
    public CustomerEntity? Customer { get; set; }

    /// <summary>Navigation: parent shift.</summary>
    [ForeignKey(nameof(ShiftId))]
    public ShiftEntity? Shift { get; set; }
}
