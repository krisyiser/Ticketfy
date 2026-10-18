using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

[Table("audit_logs")]
public class AuditLogEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("timestamp")]
    public string Timestamp { get; set; } = DateTime.Now.ToString("s");

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("authorized_by_supervisor_id")]
    public string? AuthorizedBySupervisorId { get; set; }

    [Column("action_type")]
    public AuditActionType ActionType { get; set; }

    [Column("risk_level")]
    public RiskLevel RiskLevel { get; set; }

    [Column("entity_name")]
    public string EntityName { get; set; } = string.Empty;

    [Column("entity_id")]
    public string EntityId { get; set; } = string.Empty;

    [Column("old_value")]
    public string OldValue { get; set; } = string.Empty;

    [Column("new_value")]
    public string NewValue { get; set; } = string.Empty;

    [Column("financial_impact")]
    public decimal FinancialImpact { get; set; }

    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    [Column("terminal_name")]
    public string TerminalName { get; set; } = Environment.MachineName;

    [Column("previous_hash")]
    public string PreviousHash { get; set; } = string.Empty;

    [Column("entry_hash")]
    public string EntryHash { get; set; } = string.Empty;

    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;
}
