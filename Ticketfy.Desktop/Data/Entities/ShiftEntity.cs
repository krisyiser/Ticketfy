using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Cash register shift tracking entity.
/// Tracks opening/closing balances and cash flow discrepancies.
/// Maps to legacy 'shifts' SQLite table.
/// </summary>
[Table("shifts")]
public class ShiftEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    [Column("startTime")]
    public string StartTime { get; set; } = string.Empty;

    [Column("endTime")]
    public string? EndTime { get; set; }

    [Column("openingBalance")]
    public decimal OpeningBalance { get; set; }

    [Column("totalCashSales")]
    public decimal TotalCashSales { get; set; }

    [Column("totalCreditSales")]
    public decimal TotalCreditSales { get; set; }

    [Column("expectedBalance")]
    public decimal ExpectedBalance { get; set; }

    [Column("actualBalance")]
    public decimal? ActualBalance { get; set; }

    [Column("diff")]
    public decimal? Diff { get; set; }

    [Column("isOpen")]
    public int IsOpen { get; set; } = 1;
}
