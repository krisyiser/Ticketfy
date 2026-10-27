using Ticketfy.Data.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public interface IShiftNoteService
{
    Task<List<ShiftNoteDto>> GetActiveNotesAsync();
    Task<bool> SaveNoteAsync(string cashierName, string noteText, string category = "General");
    Task<bool> UpdateNoteAsync(string id, string noteText, string category = "General");
    Task<bool> DeleteNoteAsync(string id);
    Task<bool> ResolveNoteAsync(string id);
    Task<List<ShiftNoteDto>> GetUnassignedHistoricalNotesAsync() => throw new System.NotSupportedException();
    Task AssignHistoricalNoteAsync(string id, string reason) => throw new System.NotSupportedException();
}
