using System;
using System.ComponentModel.DataAnnotations;

namespace Ticketfy.Data.Entities;

public class InventorySnapshotItemEntity : IImmutableRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SnapshotId { get; set; } = string.Empty;
    public InventorySnapshotEntity? Snapshot { get; set; }

    public string ProductId { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal CostPrice { get; set; }
    public decimal SellingPrice { get; set; }
}
