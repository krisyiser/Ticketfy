using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("shift_notes")]
public class ShiftNoteEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    [Column("cashier_name")]
    public string CashierName { get; set; } = string.Empty;

    [Required]
    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Required]
    [Column("note_text")]
    public string NoteText { get; set; } = string.Empty;

    [Column("category")]
    public string Category { get; set; } = "General";

    [Column("is_resolved")]
    public bool IsResolved { get; set; } = false;

    public string? CashierUserId { get; set; }
    public string? RegisterId { get; set; }
    public string? ShiftId { get; set; }
    public bool IsCancelled { get; set; }
}
