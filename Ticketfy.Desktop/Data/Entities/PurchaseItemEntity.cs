using System;

namespace Ticketfy.Data.Entities;

public class PurchaseItemEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PurchaseId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public double Quantity { get; set; }
    public decimal TotalPrice { get; set; }
}
