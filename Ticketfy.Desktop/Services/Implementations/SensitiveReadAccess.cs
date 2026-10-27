using Ticketfy.Core.Security;
using Ticketfy.Data;

namespace Ticketfy.Services.Implementations;

internal static class SensitiveReadAccess
{
    public static async Task<bool> CanSeeCostsAsync(AppDbContext db, CommercialOperationContext? operations)
    {
        if (operations is null) return false;
        try { await operations.RequireAsync(db, PermissionCatalog.ReportsProfits, false); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
