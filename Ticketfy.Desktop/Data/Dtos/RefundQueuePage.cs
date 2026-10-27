using Ticketfy.Data.Entities;

namespace Ticketfy.Data.Dtos;

public sealed record RefundQueueCursor(bool AccountCredit, string RegisterId, string UserId, string Search, string AfterId);
public sealed record RefundQueuePage<T>(List<T> Items, long TotalCount, RefundQueueCursor? Next);

public sealed record BankRefundHeader(string Id, string ReturnId, TenderKind Kind, decimal Amount,
    string CashierUserId, string RegisterId, string ShiftId, bool RequiresReview)
{
    public string Label => $"{ReturnId} · {Kind} · {Amount:C2} · Caja {RegisterId} · Turno {ShiftId} · Operador {CashierUserId}"
        + (RequiresReview ? " · INCONSISTENCIA: revisión técnica, no repetir dinero" : "");
}

public sealed record CreditRefundHeader(string OperationId, AccountKind AccountKind, string AccountId,
    string SourceDocumentId, decimal Amount, TenderKind TenderKind, string CashierUserId, string RegisterId,
    string ShiftId, bool RequiresReview)
{
    public string Label => $"{AccountKind} {AccountId} · {SourceDocumentId} · {Amount:C2} · {TenderKind} · {OperationId}"
        + $" · Caja {RegisterId} · Turno {ShiftId} · Operador {CashierUserId}"
        + (RequiresReview ? " · INCONSISTENCIA: revisión técnica, no repetir dinero" : "");
}
