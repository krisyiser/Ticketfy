using Ticketfy.Data.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public sealed record InventorySnapshotPage(InventorySnapshotEntity Snapshot, bool HasMore);

public interface IInventorySnapshotService
{
    Task<InventorySnapshotEntity> CreateSnapshotAsync(string notes, string operationId);
    Task<List<InventorySnapshotEntity>> GetSnapshotsAsync(int page = 0);
    Task<InventorySnapshotEntity?> GetSnapshotDetailsAsync(string snapshotId);
    Task<InventorySnapshotPage?> GetSnapshotPageAsync(string snapshotId, int page, int size = 100) => throw new System.NotSupportedException();
}
