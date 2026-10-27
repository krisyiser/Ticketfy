using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Promotion rule entity supporting product, category, and multibuy discount types.
/// Maps to legacy 'promotions' SQLite table.
/// </summary>
[Table("promotions")]
public class PromotionEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Rule type: "product", "category", or "multibuy".</summary>
    [Required]
    [Column("type")]
    public string Type { get; set; } = "product";

    [Column("strategy_type")]
    public PromotionType StrategyType { get; set; } = PromotionType.PercentageDiscount;

    [Column("target_product_id")]
    public string? TargetProductId { get; set; }

    [Column("target_category")]
    public string TargetCategory { get; set; } = string.Empty;

    [Column("targetId")]
    public string? TargetId { get; set; }

    /// <summary>Discount mode: "percent" or "fixed".</summary>
    [Column("discountType")]
    public string? DiscountType { get; set; }

    [Column("discountValue")]
    public decimal DiscountValue { get; set; }

    [Column("min_quantity")]
    public double MinQuantity { get; set; } = 1.0;

    [Column("free_quantity")]
    public double FreeQuantity { get; set; } = 0.0;

    [Column("buyQty")]
    public int BuyQty { get; set; }

    [Column("payQty")]
    public int PayQty { get; set; }

    [Column("start_date")]
    public string StartDate { get; set; } = DateTime.Now.AddDays(-1).ToString("s");

    [Column("end_date")]
    public string EndDate { get; set; } = DateTime.Now.AddMonths(1).ToString("s");

    [Column("isActive")]
    public int IsActive { get; set; } = 1;

    [Column("priority")]
    public int Priority { get; set; } = 0;

    [ConcurrencyCheck]
    [Column("revision")]
    public long Revision { get; set; }
}
