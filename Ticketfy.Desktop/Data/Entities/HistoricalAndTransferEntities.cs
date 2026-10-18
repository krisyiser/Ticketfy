using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

public enum HistoricalBalanceScope { CustomerAccount, SupplierPurchase }
public enum TransferOutcome { Applied, RefundedExternally }

[Table("historical_balance_reconciliations")]
public sealed class HistoricalBalanceReconciliationEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public HistoricalBalanceScope Scope { get; set; }
    public string TargetId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public decimal LedgerBefore { get; set; }
    public decimal? CachedBefore { get; set; }
    public decimal ConfirmedBalance { get; set; }
    public decimal Adjustment { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string EvidenceReference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

[Table("incoming_transfer_intents")]
public sealed class IncomingTransferIntentEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Notes { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>One bank deposit can fund one document only, across sales, customer payments and wallet loads.</summary>
[Table("incoming_transfer_receipts")]
public sealed class IncomingTransferReceiptEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public decimal Amount { get; set; }
    public string ExternalReference { get; set; } = "";
    public TransferOutcome Outcome { get; set; }
    public string? RefundReference { get; set; }
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
