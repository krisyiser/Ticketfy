using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("credit_refund_requests")]
public sealed class CreditRefundRequestEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public AccountKind AccountKind { get; set; }
    public string AccountId { get; set; } = "";
    public string SourceDocumentId { get; set; } = "";
    public decimal Amount { get; set; }
    public TenderKind TenderKind { get; set; }
    public string Reason { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    [NotMapped] public string Label => $"{AccountKind} · {SourceDocumentId} · {Amount:C2} · {TenderKind} · {OperationId}";
}

[Table("credit_refund_resolutions")]
public sealed class CreditRefundResolutionEntity : IImmutableRecord
{
    [Key] public string RequestId { get; set; } = "";
    public bool FundsMoved { get; set; }
    public string? ExternalReference { get; set; }
    public string Reason { get; set; } = "";
    public string ApproverUserId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
