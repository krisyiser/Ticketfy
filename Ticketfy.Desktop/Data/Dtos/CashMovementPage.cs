using Ticketfy.Core.Helpers;

namespace Ticketfy.Data.Dtos;

public sealed record CashMovementRow(string Id, decimal Amount, bool IsOutflow, string Reason, string Date,
    string UserId, string RegisterId, string ShiftId)
{
    public string TypeDisplay => IsOutflow ? "Retiro de efectivo" : "Ingreso de efectivo";
    public string AmountDisplay => $"{(IsOutflow ? "−" : "+")}{Amount:C2}";
    public string DateDisplay => BusinessTime.TryLocal(Date, out var local) ? local.ToString("dd/MM/yyyy HH:mm") : "Fecha sin verificar";
}

public sealed record CashMovementPage(List<CashMovementRow> Items, bool HasMore, int TotalCount, ShiftDto Shift);
