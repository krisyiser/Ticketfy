using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Interfaces;

public interface IBackupService
{
    Task<string> CreateBackupAsync(string reference = "Manual", CancellationToken cancellationToken = default);
    Task<bool> CreateZCutBackupAsync(string shiftReference, CancellationToken cancellationToken = default);
    Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<BackupValidationResult> TestRestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<RestorePreparationResult> PrepareRestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken = default);
    Task<BackupMaintenanceResult> RunMaintenanceAsync(BackupPolicy policy, CancellationToken cancellationToken = default);
}
