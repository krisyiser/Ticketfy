namespace Ticketfy.Data.Dtos;

public sealed record ExpenseShiftSnapshot(string Id, string StartTime, decimal? OpeningBalance, decimal? ExpectedBalance);

/// <summary>Rows, whole-period financial summary and current-register balances from one authorized read snapshot.</summary>
public sealed record ExpensePageDto(
    List<ExpenseDto> Items,
    int TotalCount,
    bool HasMore,
    int UnparseableDateCount,
    FinancialSummaryDto? Summary,
    ExpenseShiftSnapshot? ActiveShift,
    bool CashVisible)
{
    public List<ExpenseCancellationDto> Cancellations { get; init; } = [];
    public decimal CancellationAmount { get; init; }
}

/// <summary>Receipt of a reversal and its original expense, read in the same snapshot.</summary>
public sealed record ExpenseCancellationDto(string Id, string? OperationId, string Date, decimal Amount,
    string? Reason, string? UserId, string? RegisterId, string? ShiftId, bool IsLegacy, ExpenseDto Original)
{
    public string DateDisplay => Ticketfy.Core.Helpers.BusinessTime.TryLocal(Date, out var date)
        ? date.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "Fecha sin verificar";
    public string EvidenceDisplay => IsLegacy
        ? "Heredado sin diario: no se puede atribuir el reverso a un usuario, caja o turno."
        : "Reverso registrado en el diario; no equivale a una validación bancaria independiente.";
    public string UserDisplay => string.IsNullOrWhiteSpace(UserId) ? "Sin atribución verificable" : UserId;
    public string RegisterDisplay => string.IsNullOrWhiteSpace(RegisterId) ? "Sin atribución verificable" : RegisterId;
    public string ShiftDisplay => string.IsNullOrWhiteSpace(ShiftId) ? "Sin atribución verificable" : ShiftId;
}
