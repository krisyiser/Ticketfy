namespace Ticketfy.Data.Dtos;

public sealed record HourlySaleTotal(int Hour, int TicketCount, decimal GrossAmount);

/// <summary>Only visible documents are hydrated; issuance totals cover the complete requested period.</summary>
public sealed record SalesHistoryPage(List<SaleDto> Items, int TotalCount, bool HasMore,
    List<HourlySaleTotal> Hours, int UnparseableDateCount);
