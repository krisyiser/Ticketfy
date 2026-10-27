using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ticketfy.Core.Models;

public partial class CashierProductivityReportModel : ObservableObject
{
    public string UserId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public double TotalHoursWorked { get; set; }
    public int TotalTicketsProcessed { get; set; }
    public decimal GrossSales { get; set; }
    public decimal AverageTicketValue { get; set; }
    public decimal TrueSalesPerHour { get; set; }
    public double TicketsPerHour { get; set; }
    public bool AttributionVerified { get; set; } = true;
    public bool HasVerifiedHours { get; set; }
    public int AttendanceIssueCount { get; set; }
    public string TrueSalesPerHourDisplay => HasVerifiedHours ? TrueSalesPerHour.ToString("C2") : "No calculable";
    public string TicketsPerHourDisplay => HasVerifiedHours ? TicketsPerHour.ToString("F2") : "No calculable";
    public string AttendanceStatusDisplay => !AttributionVerified ? "Usuario histórico sin verificar"
        : AttendanceIssueCount > 0 ? $"{AttendanceIssueCount} asistencias por revisar"
        : HasVerifiedHours ? "Asistencia completada" : "Sin horas verificadas";
    public string EstimatedCommissionDisplay => AttributionVerified ? EstimatedCommission.ToString("C2") : "No atribuible";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedCommissionDisplay))]
    private decimal _estimatedCommission;
}
