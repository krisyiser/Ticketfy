using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("cashups")]
public class CashupEntity
{
    public string? OperationId { get; set; }
    public string? RegisterId { get; set; }
    public string? CashierUserId { get; set; }
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("shift_id")]
    public string? ShiftId { get; set; }

    [Column("open_cash_amount")]
    public decimal OpenCashAmount { get; set; }

    [Column("closed_cash_amount")]
    public decimal ClosedCashAmount { get; set; }

    [Column("count_1000")]
    public int Count1000 { get; set; }

    [Column("count_500")]
    public int Count500 { get; set; }

    [Column("count_200")]
    public int Count200 { get; set; }

    [Column("count_100")]
    public int Count100 { get; set; }

    [Column("count_50")]
    public int Count50 { get; set; }

    [Column("count_20")]
    public int Count20 { get; set; }

    [Column("count_10")]
    public int Count10 { get; set; }

    [Column("count_5")]
    public int Count5 { get; set; }

    [Column("count_2")]
    public int Count2 { get; set; }

    [Column("count_1")]
    public int Count1 { get; set; }

    [Column("count_050")]
    public int Count050 { get; set; }

    [Column("theoretical_cash")]
    public decimal TheoreticalCash { get; set; }

    [Column("difference")]
    public decimal Difference { get; set; }

    [Column("notes")]
    public string Notes { get; set; } = string.Empty;

    [Column("type")]
    public string Type { get; set; } = "Final";

    [Column("cashier_name")]
    public string CashierName { get; set; } = "Cajero en turno";

    [Column("cashier_role")]
    public string CashierRole { get; set; } = "CAJERO";

    [Column("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [NotMapped]
    public decimal? TotalSales { get; set; }

    [NotMapped]
    public decimal? TotalExpenses { get; set; }
    [NotMapped]
    public string LocalDateDisplay => Ticketfy.Core.Helpers.DateTimeExtensions.ToLocalDisplayString(Timestamp);
    [NotMapped]
    public string TotalSalesDisplay => TotalSales?.ToString("C2") ?? "Sin desglose";
    [NotMapped]
    public string TotalExpensesDisplay => TotalExpenses?.ToString("C2") ?? "Sin desglose";
    [NotMapped]
    public string BreakdownStatus => TotalSales.HasValue && TotalExpenses.HasValue ? "Desglose verificable" : "Requiere conciliación histórica";
    [NotMapped]
    public Ticketfy.Core.Models.CashDenominationCount Denominations => new(Count1000, Count500, Count200, Count100,
        Count50, Count20, Count10, Count5, Count2, Count1, Count050);
    [NotMapped]
    public string DenominationStatus => !Denominations.IsConsistentWith(ClosedCashAmount) ? "Desglose inconsistente: requiere revisión"
        : Denominations.HasPieces ? "Conteo por denominaciones" : "Conteo agregado: sin detalle de piezas";
}
