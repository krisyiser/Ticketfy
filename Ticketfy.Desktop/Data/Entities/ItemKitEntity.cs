using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("item_kits")]
public class ItemKitEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("parent_product_id")]
    public string ParentProductId { get; set; } = string.Empty;

    [Required]
    [Column("kit_barcode")]
    public string KitBarcode { get; set; } = string.Empty;

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [NotMapped]
    public string KitName => Name;

    [Column("price")]
    public decimal Price { get; set; }

    [Column("description")]
    public string Description { get; set; } = string.Empty;

    [Column("created_at")]
    public string CreatedAt { get; set; } = DateTime.Now.ToString("s");

    /// <summary>Navigation: components / bill of materials for this kit</summary>
    public ICollection<ItemKitItemEntity> Components { get; set; } = [];

    [ForeignKey(nameof(ParentProductId))]
    public ProductEntity? ParentProduct { get; set; }
}

[Table("item_kit_items")]
public class ItemKitItemEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("item_kit_id")]
    public string ItemKitId { get; set; } = string.Empty;

    [Required]
    [Column("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [NotMapped]
    public string IngredientProductId => ProductId;

    [Column("quantity")]
    public double Quantity { get; set; }

    [NotMapped]
    public decimal QuantityRequired => (decimal)Quantity;

    [ForeignKey(nameof(ItemKitId))]
    public ItemKitEntity? ItemKit { get; set; }

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? IngredientProduct { get; set; }
}
