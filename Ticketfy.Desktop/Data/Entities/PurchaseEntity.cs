using System;

namespace Ticketfy.Data.Entities;

public class PurchaseEntity
{
    public string? OperationId { get; set; }
    public string? ShiftId { get; set; }
    public string? RegisterId { get; set; }
    public string? CashierUserId { get; set; }
    public string? DueDate { get; set; }
    public int LedgerVersion { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SupplierId { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public decimal TotalCost { get; set; }
    public string Notes { get; set; } = string.Empty;
}
