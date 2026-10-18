using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Product catalog entity. Maps to legacy 'products' SQLite table.
/// Tracks inventory items with cost/price/wholesale tiers, stock levels, kits, and supplier links.
/// </summary>
[Table("products")]
public class ProductEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("barcode")]
    public string? Barcode { get; set; }

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("cost")]
    public decimal Cost { get; set; }

    [Required]
    [Column("price")]
    public decimal Price { get; set; }

    [Column("wholesalePrice")]
    public decimal WholesalePrice { get; set; }

    [Column("wholesaleThreshold")]
    public int WholesaleThreshold { get; set; }

    [Column("stock")]
    [ConcurrencyCheck]
    public double Stock { get; set; }

    [ConcurrencyCheck]
    [Column("stock_version")]
    public long StockVersion { get; set; }

    [Required]
    [Column("category")]
    public string Category { get; set; } = "Abarrotes";

    [Required]
    [Column("unit")]
    public string Unit { get; set; } = "Pza";

    [Column("expiresSoon")]
    public int ExpiresSoon { get; set; }

    [Column("minStock")]
    public double MinStock { get; set; }

    [Column("is_kit")]
    public bool IsKit { get; set; } = false;

    [Column("is_bulk")]
    public bool IsBulk { get; set; } = false;

    [Column("default_supplier_id")]
    public string? DefaultSupplierId { get; set; }

    [Column("points_rewarded")]
    public double PointsRewarded { get; set; } = 0.0;

    [Column("reorder_quantity")]
    public double ReorderQuantity { get; set; } = 10.0;

    [Column("location_rack")]
    public string LocationRack { get; set; } = "Pasillo 1 - Anaquel A";

    [Column("sat_product_code")]
    public string SatProductCode { get; set; } = "01010101";

    [Column("sat_unit_code")]
    public string SatUnitCode { get; set; } = "H87";

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [NotMapped]
    public bool IsOutOfStock => Stock <= 0.0;

    [NotMapped]
    public bool IsAvailable => Stock > 0.0;

    [NotMapped]
    public decimal CostPrice
    {
        get => Cost;
        set => Cost = value;
    }

    [NotMapped]
    public decimal StockDecimal
    {
        get => (decimal)Stock;
        set => Stock = (double)value;
    }

    [NotMapped]
    public decimal MinStockDecimal
    {
        get => (decimal)MinStock;
        set => MinStock = (double)value;
    }

    [ForeignKey(nameof(DefaultSupplierId))]
    public SupplierEntity? DefaultSupplier { get; set; }
}
