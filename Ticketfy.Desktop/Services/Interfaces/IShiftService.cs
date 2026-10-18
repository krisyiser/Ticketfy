using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

/// <summary>
/// Cash register shift lifecycle: open, close, and query history.
/// </summary>
public interface IShiftService
{
    Task<ShiftDto?> GetActiveAsync();
    Task<ShiftDto> OpenAsync(decimal openingBalance);
    Task<ShiftDto> CloseAsync(string shiftId, decimal actualBalance);
    Task<List<ShiftDto>> GetAllAsync();
}
