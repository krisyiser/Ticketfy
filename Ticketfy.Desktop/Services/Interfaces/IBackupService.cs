using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public interface IBackupService
{
    Task<string> CreateBackupAsync(string reference = "Manual");
    Task<bool> CreateZCutBackupAsync(string shiftReference);
}
