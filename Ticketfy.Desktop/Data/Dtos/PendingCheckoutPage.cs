namespace Ticketfy.Data.Dtos;

/// <summary>Navigation position only, never an authorization token or a frozen bank state.</summary>
public sealed record PendingCheckoutCursor(string RegisterId, string Search, string AfterOperationId);

public sealed record PendingCheckoutPage(List<PendingCheckoutDto> Items, long TotalCount, PendingCheckoutCursor? Next);
