using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("shift_note_revisions")]
public sealed class ShiftNoteRevisionEntity : IImmutableRecord
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NoteId { get; set; } = "";
    public string Action { get; set; } = "";
    public string NoteText { get; set; } = "";
    public string Category { get; set; } = "";
    public string CashierUserId { get; set; } = "";
    public string RegisterId { get; set; } = "";
    public string? ShiftId { get; set; }
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
