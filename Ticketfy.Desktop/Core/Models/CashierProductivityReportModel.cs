using System;

namespace Ticketfy.Core.Models;

public class CashierProductivityReportModel
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
    public decimal EstimatedCommission { get; set; }
}
