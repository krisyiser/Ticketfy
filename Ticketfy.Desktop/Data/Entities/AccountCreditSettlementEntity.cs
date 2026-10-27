using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

public enum AccountCreditSettlementKind { Application, Refund }

/// <summary>A compensating settlement; source sales, purchases, payments and returns never change.</summary>
[Table("account_credit_settlements")]
public sealed class AccountCreditSettlementEntity : IImmutableRecord
{
    [Key] public string OperationId { get; set; } = "";
    public AccountKind AccountKind { get; set; }
    public string AccountId { get; set; } = "";
    public string SourceDocumentId { get; set; } = "";
    public string? TargetDocumentId { get; set; }
    public AccountCreditSettlementKind Kind { get; set; }
    public decimal Amount { get; set; }
    public TenderKind? TenderKind { get; set; }
    public string? ExternalReference { get; set; }
    public string Reason { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
