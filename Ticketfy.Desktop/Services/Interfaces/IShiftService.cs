using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public sealed record ShiftCashSummary(ShiftDto Shift, decimal Inflows, decimal Outflows);
public sealed record CashupHistoryPage(List<Ticketfy.Data.Entities.CashupEntity> Items, int TotalCount, bool HasMore, int UnparseableDateCount);

/// <summary>
/// Cash register shift lifecycle: open, close, and query history.
/// </summary>
public interface IShiftService
{
    Task<ShiftDto?> GetActiveAsync();
    Task<ShiftCashSummary?> GetActiveCashSummaryAsync() => throw new NotSupportedException();
    Task<ShiftDto> OpenAsync(decimal openingBalance);
    Task<ShiftDto> OpenAsync(decimal openingBalance, string operationId) => OpenAsync(openingBalance);
    Task<ShiftDto> CloseAsync(string shiftId, decimal actualBalance);
    Task<ShiftDto> CloseWithCashupAsync(string shiftId, Ticketfy.Data.Entities.CashupEntity cashup) => throw new NotSupportedException();
    Task RecordCountAsync(string shiftId, Ticketfy.Data.Entities.CashupEntity cashup) => throw new NotSupportedException();
    Task RegisterMovementAsync(decimal amount, bool outflow, string reason, string operationId) => throw new NotSupportedException();
    Task<List<ShiftDto>> GetAllAsync();
    Task<List<Ticketfy.Data.Entities.CashupEntity>> GetCashupsAsync(DateTime start, DateTime end) => throw new NotSupportedException();
    Task<CashupHistoryPage> GetCashupPageAsync(DateTime start, DateTime end, int page, int size = 100) => throw new NotSupportedException();
    Task<List<ShiftDto>> GetLegacyOpenAsync() => throw new NotSupportedException();
    Task ReconcileLegacyAsync(string shiftId, decimal countedCash, string reason, string operationId) => throw new NotSupportedException();
}
