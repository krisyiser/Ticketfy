using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Key-value settings persistence entity.
/// Replaces localStorage-backed settings from the legacy web app.
/// Maps to legacy 'settings' SQLite table.
/// </summary>
[Table("settings")]
public class SettingEntity
{
    [Key]
    [Column("key")]
    public string Key { get; set; } = string.Empty;

    [Required]
    [Column("value")]
    public string Value { get; set; } = string.Empty;
}
