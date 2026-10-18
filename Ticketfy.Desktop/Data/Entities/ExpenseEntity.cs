using System;

namespace Ticketfy.Data.Entities;

public class ExpenseEntity
{
    public string? OperationId { get; set; }
    public string? ShiftId { get; set; }
    public string? RegisterId { get; set; }
    public bool IsCancelled { get; set; }
    public string? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }
    public string? Reference { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Category { get; set; } = "General";
    public decimal Amount { get; set; }
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string Description { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "Efectivo";
    public string RegisteredByUser { get; set; } = string.Empty;
}
