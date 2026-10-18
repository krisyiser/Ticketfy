using System;

namespace Ticketfy.Core.Models;

public class ShiftMovementSlipModel
{
    public string Folio { get; set; } = string.Empty;
    public string MovementTypeLabel { get; set; } = "MOVIMIENTO DE CAJA";
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CashierName { get; set; } = "Cajero Matrix";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
