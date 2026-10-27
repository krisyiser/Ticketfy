using Ticketfy.Data.Entities;

namespace Ticketfy.Data.Dtos;

/// <summary>A visibility cut, scoped to one ledger and account. It is not an authorization token.</summary>
public sealed record LedgerReadCut(string Ledger, string OwnerId, long ThroughSequence, string CapturedAtUtc);

/// <summary>Page zero contains the most recent entries; each page is presented in posting order.</summary>
public sealed record AccountStatementPageDto(string AccountId, decimal Balance, decimal OpeningBalance,
    List<AccountEntryEntity> Entries, LedgerReadCut Cut, long TotalCount, bool HasMore,
    bool HasNewerEntries, bool HasUnreconciledHistory, bool IncludesMigrationOpening, decimal? RecordedBalance);

public sealed record InventoryLedgerPageDto(string ProductId, List<InventoryMovementEntity> Entries,
    LedgerReadCut Cut, long TotalCount, bool HasMore, bool HasNewerEntries, decimal? LedgerStock,
    decimal RecordedStock, bool HasUnreconciledHistory, bool IncludesMigrationOpening, bool CostsVisible);
