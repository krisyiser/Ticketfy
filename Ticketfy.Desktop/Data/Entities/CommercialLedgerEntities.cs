using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("command_receipts")]
public sealed class CommandReceiptEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Command { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string? ShiftId { get; set; }
}

public enum TenderKind { Cash, Card, Giftcard, Credit, Transfer, Loyalty }
public enum TenderState { Pending, Authorized, Captured, Rejected, Cancelled, PartiallyRefunded, Refunded, RequiresReconciliation }
public enum AccountKind { Customer, Supplier, Giftcard, Loyalty }
public interface IImmutableRecord { }

[Table("tender_confirmations")]
public sealed class TenderConfirmationEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public TenderKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string ExternalReference { get; set; } = "";
    public string Purpose { get; set; } = "CAPTURE";
    public string ShiftId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("refund_settlements")]
public sealed class RefundSettlementEntity : IImmutableRecord
{
    [Key] public string RefundId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string ExternalReference { get; set; } = "";
    public decimal Amount { get; set; }
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("sale_payments")]
public sealed class SalePaymentEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SaleId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public TenderKind Kind { get; set; }
    public decimal Amount { get; set; }
    public TenderState State { get; set; } = TenderState.Captured;
    public string? ExternalReference { get; set; }
    public string? AccountId { get; set; }
    public string ShiftId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("sale_lines")]
public sealed class SaleLineEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SaleId { get; set; } = "";
    public int LineNumber { get; set; }
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Total { get; set; }
    public decimal CostTotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public string Unit { get; set; } = "Pza";
    public string? PromotionId { get; set; }
}

[Table("return_lines")]
public sealed class ReturnLineEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ReturnId { get; set; } = "";
    public string SaleLineId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public decimal CostReversed { get; set; }
    public bool Restocked { get; set; }
}

[Table("payment_refunds")]
public sealed class PaymentRefundEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ReturnId { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public TenderKind Kind { get; set; }
    public decimal Amount { get; set; }
    public TenderState State { get; set; } = TenderState.Refunded;
    public string? ExternalReference { get; set; }
    public string ShiftId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("inventory_movements")]
public sealed class InventoryMovementEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long PostingSequence { get; set; }
    public string ProductId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string? SaleLineId { get; set; }
    public string Reason { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal StockBefore { get; set; }
    public decimal StockAfter { get; set; }
    public decimal UnitCost { get; set; }
    public string? ShiftId { get; set; }
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>Positive = debt/available wallet points increases; negative = payment/redemption/reversal.</summary>
[Table("account_entries")]
public sealed class AccountEntryEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long PostingSequence { get; set; }
    public AccountKind AccountKind { get; set; }
    public string AccountId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string EntryType { get; set; } = "";
    public decimal Amount { get; set; }
    public string? DueDate { get; set; }
    public string? ShiftId { get; set; }
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("supplier_payments")]
public sealed class SupplierPaymentEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string PurchaseId { get; set; } = "";
    public decimal Amount { get; set; }
    public TenderKind Kind { get; set; }
    public string? Reference { get; set; }
    public string ShiftId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("supplier_returns")]
public sealed class SupplierReturnEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; set; } = "";
    public string PurchaseId { get; set; } = "";
    public string PurchaseItemId { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
