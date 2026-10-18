using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("returns")]
public class ReturnEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("original_sale_id")]
    public string OriginalSaleId { get; set; } = string.Empty;

    [Column("cashier_user_id")]
    public string? CashierUserId { get; set; }

    [Column("total_refunded")]
    public decimal TotalRefunded { get; set; }

    [Column("cogs_reversed")]
    public decimal CogsReversed { get; set; }

    [Required]
    [Column("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [Column("status")]
    public Ticketfy.Core.Enums.ReturnStatus Status { get; set; } = Ticketfy.Core.Enums.ReturnStatus.Completed;

    [Column("refund_method")]
    public string RefundMethod { get; set; } = "Efectivo";

    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    [Column("created_at")]
    public string CreatedAt { get; set; } = DateTime.Now.ToString("s");

    [ForeignKey(nameof(OriginalSaleId))]
    public SaleEntity? OriginalSale { get; set; }
}
