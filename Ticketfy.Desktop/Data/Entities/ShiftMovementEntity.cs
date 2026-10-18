using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

[Table("shift_movements")]
public class ShiftMovementEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("shift_id")]
    public string ShiftId { get; set; } = string.Empty;

    [Column("movement_type")]
    public MovementType MovementType { get; set; } = MovementType.AbonoCliente;

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("is_outflow")]
    public bool IsOutflow { get; set; } = false;

    [Column("description")]
    public string Description { get; set; } = string.Empty;

    [Column("reference_id")]
    public string ReferenceId { get; set; } = string.Empty;

    [Column("timestamp")]
    public string Timestamp { get; set; } = DateTime.Now.ToString("s");

    [ForeignKey(nameof(ShiftId))]
    public ShiftEntity? Shift { get; set; }
}
