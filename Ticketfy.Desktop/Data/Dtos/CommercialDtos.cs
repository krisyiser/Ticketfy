using Ticketfy.Data.Entities;

namespace Ticketfy.Data.Dtos;

public sealed record SaleTenderDto(TenderKind Kind, decimal Amount, string? Reference = null, string? AccountId = null);
public sealed record HistoricalBalanceTargetDto(string Id, string Label);
public sealed record HistoricalBalancePreview(HistoricalBalanceScope Scope, string TargetId, string AccountId,
    string Label, decimal LedgerBalance, decimal? CachedBalance, decimal? MaximumBalance, string Fingerprint);
public sealed record PendingTransferDto(string OperationId, string Purpose, string DestinationId, decimal Amount, string? Reference)
{
    public string? CashierUserId { get; init; }
    public string? ShiftId { get; init; }
    public string Label => $"{OperationId} · {Purpose} · {DestinationId} · {Amount:C2} · {(Reference is null ? "Sin verificar" : "Verificada; pendiente de aplicar")} · Cajero: {CashierUserId} · Turno: {ShiftId}";
}
public sealed record PendingCheckoutDto(string OperationId, decimal Amount, string Method, string State, string? RegisterId, string? CashierUserId);
public sealed record AccountStatementDto(string AccountId, decimal Balance, List<AccountEntryEntity> Entries, bool HasUnreconciledHistory = false);
public sealed record ReconciliationDto(string ShiftId, decimal? ExpectedCash, decimal? CountedCash,
    decimal? Difference, decimal? Sales, decimal? Payments, decimal? Refunds, decimal? PendingRefunds, bool IsBalanced)
{
    public string? RegisterId { get; init; }
    public string? CashierUserId { get; init; }
    public IReadOnlyList<ReconciliationIssueDto> Issues { get; init; } = [];
}
public sealed record ReconciliationPage(List<ReconciliationDto> Items, long TotalCount, bool HasMore);
public sealed record ReconciliationIssueDto(string Code, string Message);
