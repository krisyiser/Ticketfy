using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Append-only operational audit written in the same SQLite transaction as the
/// money and inventory mutation it describes.
/// </summary>
[Table("transaction_journal")]
public sealed class TransactionJournalEntryEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    [Column("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    [Required]
    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Required]
    [Column("entity_id")]
    public string EntityId { get; set; } = string.Empty;

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("details")]
    public string Details { get; set; } = string.Empty;

    [Column("created_at_utc")]
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("o");
}
