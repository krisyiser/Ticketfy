using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class CommercialReconciliationService
{
    private static IQueryable<PaymentRefundEntity> PendingBankRefunds(AppDbContext db) =>
        db.PaymentRefunds.AsNoTracking().Where(r => r.State == TenderState.RequiresReconciliation &&
            !db.RefundSettlements.Any(s => s.RefundId == r.Id && s.Amount == r.Amount &&
                s.ExternalReference.Trim() != "" && s.CashierUserId.Trim() != "" && s.RegisterId.Trim() != "" && s.ShiftId.Trim() != ""));

    // Compatibility only: callers must use pages above the explicit limit, never a silent truncation.
    public async Task<List<PaymentRefundEntity>> GetPendingRefundsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        var rows = await PendingBankRefunds(db).OrderBy(r => EF.Functions.Collate(r.Id, "BINARY")).Take(201).ToListAsync();
        if (await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        if (rows.Count > 200) throw new InvalidOperationException("Hay más de 200 pendientes. Use la consulta paginada para revisarlos todos.");
        return rows;
    }

    public async Task<RefundQueuePage<BankRefundHeader>> GetPendingRefundsPageAsync(int size = 100, string? search = null,
        RefundQueueCursor? after = null)
    {
        search = RefundQueueQuery.Validate(size, search);
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        RefundQueueQuery.ValidateCursor(after, false, actor, search);
        // This reconciliation queue intentionally covers all registers, with explicit origin attribution.
        var query = PendingBankRefunds(db);
        if (search.Length > 0) query = query.Where(r => r.Id.Contains(search) || r.ReturnId.Contains(search) ||
            r.RegisterId.Contains(search) || r.ShiftId.Contains(search) || r.CashierUserId.Contains(search));
        long count = await query.LongCountAsync();
        if (after is not null) query = query.Where(r => string.Compare(EF.Functions.Collate(r.Id, "BINARY"), after.AfterId) > 0);
        var rows = await query.OrderBy(r => EF.Functions.Collate(r.Id, "BINARY")).Take(size + 1)
            .Select(r => new BankRefundHeader(r.Id, r.ReturnId, r.Kind, r.Amount, r.CashierUserId, r.RegisterId, r.ShiftId,
                db.RefundSettlements.Any(s => s.RefundId == r.Id))).ToListAsync();
        bool more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        if (await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return new(rows, count, more ? new(false, actor.RegisterId, actor.UserId, search, rows[^1].Id) : null);
    }
}
