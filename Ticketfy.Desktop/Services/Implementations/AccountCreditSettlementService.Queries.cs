using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class AccountCreditSettlementService
{
    public async Task<List<CreditRefundRequestEntity>> GetPendingRefundsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false);
        var rows = await PendingRequests(db).Where(r => r.RegisterId == actor.RegisterId)
            .OrderBy(r => EF.Functions.Collate(r.OperationId, "BINARY")).Take(201).ToListAsync();
        if (await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        if (rows.Count > 200) throw new InvalidOperationException("Hay más de 200 solicitudes. Use la consulta paginada para revisarlas todas.");
        return rows;
    }

    public async Task<RefundQueuePage<CreditRefundHeader>> GetPendingRefundsPageAsync(int size = 100, string? search = null,
        RefundQueueCursor? after = null)
    {
        search = RefundQueueQuery.Validate(size, search);
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false);
        RefundQueueQuery.ValidateCursor(after, true, actor, search);
        var query = PendingRequests(db).Where(r => r.RegisterId == actor.RegisterId);
        if (search.Length > 0) query = query.Where(r => r.OperationId.Contains(search) || r.AccountId.Contains(search) ||
            r.SourceDocumentId.Contains(search) || r.ShiftId.Contains(search) || r.CashierUserId.Contains(search));
        long count = await query.LongCountAsync();
        if (after is not null) query = query.Where(r => string.Compare(EF.Functions.Collate(r.OperationId, "BINARY"), after.AfterId) > 0);
        var rows = await query.OrderBy(r => EF.Functions.Collate(r.OperationId, "BINARY")).Take(size + 1)
            .Select(r => new CreditRefundHeader(r.OperationId, r.AccountKind, r.AccountId, r.SourceDocumentId, r.Amount,
                r.TenderKind, r.CashierUserId, r.RegisterId, r.ShiftId,
                db.CreditRefundResolutions.Any(s => s.RequestId == r.OperationId) ||
                db.AccountCreditSettlements.Any(s => s.OperationId == r.OperationId) ||
                db.AccountEntries.Any(s => s.OperationId == r.OperationId) ||
                db.ShiftMovements.Any(s => s.OperationId == r.OperationId))).ToListAsync();
        bool more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        if (await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return new(rows, count, more ? new(true, actor.RegisterId, actor.UserId, search, rows[^1].OperationId) : null);
    }
}
