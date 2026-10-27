using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class IncomingTransferService
{
    public async Task<TransferQueuePage<PendingTransferDto>> GetPendingPageAsync(int size = 100, string? search = null,
        TransferQueueCursor? after = null)
    {
        search = ValidateQueueSearch(size, search);
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var access = await AuthorizeQueueAsync(db, false);
        ValidateQueueCursor(after, false, access, search);
        var query = PendingIntents(db).AsNoTracking().Where(i => i.RegisterId == access.Actor.RegisterId &&
            (access.Supervisory || i.CashierUserId == access.Actor.UserId));
        if (search.Length > 0) query = query.Where(i => i.OperationId.Contains(search) || i.DestinationId.Contains(search));
        long count = await query.LongCountAsync();
        if (after is not null)
            query = query.Where(i => string.Compare(EF.Functions.Collate(i.OperationId, "BINARY"), after.AfterOperationId) > 0);
        var rows = await query.OrderBy(i => EF.Functions.Collate(i.OperationId, "BINARY")).Take(size + 1)
            .Select(i => new PendingTransferDto(i.OperationId, i.Purpose, i.DestinationId, i.Amount,
                db.TenderConfirmations.Where(c => c.OperationId == i.OperationId).Select(c => c.ExternalReference).FirstOrDefault())
            { CashierUserId = i.CashierUserId, ShiftId = i.ShiftId }).ToListAsync();
        bool more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        if (await AuthorizeQueueAsync(db, false) != access)
            throw new UnauthorizedAccessException("La sesión o sus permisos cambiaron. Vuelva a consultar.");
        return new(rows, count, more ? new(false, access.Actor.RegisterId, access.Actor.UserId,
            access.Supervisory, search, rows[^1].OperationId) : null);
    }

    public async Task<TransferQueuePage<IncomingTransferReceiptEntity>> GetReceiptsPageAsync(int size = 100,
        string? search = null, TransferQueueCursor? after = null)
    {
        search = ValidateQueueSearch(size, search);
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var access = await AuthorizeQueueAsync(db, true);
        ValidateQueueCursor(after, true, access, search);
        // The historical report retains its multi-register scope and explicit attribution.
        var query = db.IncomingTransferReceipts.AsNoTracking();
        if (search.Length > 0) query = query.Where(r => r.OperationId.Contains(search) || r.DocumentId.Contains(search) ||
            r.ExternalReference.Contains(search) || r.RefundReference != null && r.RefundReference.Contains(search));
        long count = await query.LongCountAsync();
        if (after is not null)
            query = query.Where(r => string.Compare(EF.Functions.Collate(r.OperationId, "BINARY"), after.AfterOperationId) > 0);
        var rows = await query.OrderBy(r => EF.Functions.Collate(r.OperationId, "BINARY")).Take(size + 1).ToListAsync();
        bool more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        if (await AuthorizeQueueAsync(db, true) != access)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return new(rows, count, more ? new(true, access.Actor.RegisterId, access.Actor.UserId, false,
            search, rows[^1].OperationId) : null);
    }

    private async Task<(OperationIdentity Actor, bool Supervisory)> AuthorizeQueueAsync(AppDbContext db, bool receipts)
    {
        string permission = receipts ? PermissionCatalog.ReportsView : PermissionCatalog.PaymentsReconcile;
        var actor = await operations.RequireAsync(db, permission, false);
        bool supervisory = false;
        if (!receipts)
            try { await operations.RequireAsync(db, PermissionCatalog.CheckoutRecover, false); supervisory = true; }
            catch (UnauthorizedAccessException) { }
        if (await operations.RequireAsync(db, permission, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió durante la consulta.");
        return (actor, supervisory);
    }

    private static string ValidateQueueSearch(int size, string? search)
    {
        if (size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(size));
        if (search?.Any(char.IsControl) == true) throw new ArgumentException("La búsqueda no admite caracteres de control.", nameof(search));
        search = search?.Trim() ?? "";
        if (search.Length > 128) throw new ArgumentException("La búsqueda admite hasta 128 caracteres.", nameof(search));
        return search;
    }

    private static void ValidateQueueCursor(TransferQueueCursor? cursor, bool receipts,
        (OperationIdentity Actor, bool Supervisory) access, string search)
    {
        if (cursor is not null && (cursor.Receipts != receipts || cursor.RegisterId != access.Actor.RegisterId ||
            cursor.UserId != access.Actor.UserId || cursor.Supervisory != access.Supervisory || cursor.Search != search ||
            string.IsNullOrEmpty(cursor.AfterOperationId)))
            throw new ArgumentException("La posición pertenece a otra consulta, caja o usuario. Vuelva a buscar.", nameof(cursor));
    }
}
