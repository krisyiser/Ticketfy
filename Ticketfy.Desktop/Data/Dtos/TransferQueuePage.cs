namespace Ticketfy.Data.Dtos;

// Navigation state, not an authorization token or a snapshot of bank evidence.
public sealed record TransferQueueCursor(bool Receipts, string RegisterId, string UserId, bool Supervisory,
    string Search, string AfterOperationId);
public sealed record TransferQueuePage<T>(List<T> Items, long TotalCount, TransferQueueCursor? Next);
