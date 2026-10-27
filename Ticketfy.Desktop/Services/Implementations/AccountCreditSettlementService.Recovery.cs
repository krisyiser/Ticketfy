using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class AccountCreditSettlementService
{
    // A resolution alone is not proof that money and its ledgers were committed.
    // Use this same predicate for reservations, recovery and the shift-close barrier.
    internal static IQueryable<CreditRefundRequestEntity> PendingRequests(AppDbContext db) =>
        db.CreditRefundRequests.AsNoTracking().Where(r => !db.CreditRefundResolutions.Any(s =>
            s.RequestId == r.OperationId && s.ApproverUserId.Trim() != "" &&
            (!s.FundsMoved
                ? s.ExternalReference == null && !db.AccountCreditSettlements.Any(a => a.OperationId == r.OperationId) &&
                    !db.AccountEntries.Any(a => a.OperationId == r.OperationId) &&
                    !db.ShiftMovements.Any(m => m.OperationId == r.OperationId)
                : db.AccountCreditSettlements.Any(a => a.OperationId == r.OperationId &&
                    a.Kind == AccountCreditSettlementKind.Refund && a.AccountKind == r.AccountKind &&
                    a.AccountId == r.AccountId && a.SourceDocumentId == r.SourceDocumentId && a.TargetDocumentId == null &&
                    a.Amount == r.Amount && a.TenderKind == r.TenderKind && a.ExternalReference == s.ExternalReference &&
                    a.RegisterId == r.RegisterId && a.ShiftId == r.ShiftId && a.CashierUserId == r.CashierUserId) &&
                    db.AccountEntries.Any(a => a.OperationId == r.OperationId && a.EntryType == "CREDIT_REFUNDED" &&
                        a.AccountKind == r.AccountKind && a.AccountId == r.AccountId && a.DocumentId == r.SourceDocumentId &&
                        a.Amount == r.Amount && a.RegisterId == r.RegisterId && a.ShiftId == r.ShiftId && a.CashierUserId == r.CashierUserId) &&
                    (r.TenderKind == TenderKind.Cash
                        ? s.ExternalReference == null && db.ShiftMovements.Any(m => m.OperationId == r.OperationId &&
                            m.ReferenceId == r.OperationId && m.Amount == r.Amount && m.ShiftId == r.ShiftId &&
                            m.RegisterId == r.RegisterId && m.CashierUserId == r.CashierUserId &&
                            m.IsOutflow == (r.AccountKind == AccountKind.Customer) &&
                            m.MovementType == (r.AccountKind == AccountKind.Customer ? MovementType.ReembolsoSaldoCliente : MovementType.ReembolsoProveedor))
                        : (r.TenderKind == TenderKind.Card || r.TenderKind == TenderKind.Transfer) &&
                            s.ExternalReference != null && s.ExternalReference.Trim() != "" &&
                            !db.ShiftMovements.Any(m => m.OperationId == r.OperationId)))));

    internal static Task<bool> HasPendingAsync(AppDbContext db, string shiftId) =>
        PendingRequests(db).AnyAsync(r => r.ShiftId == shiftId);

    public async Task PrepareRefundAsync(AccountKind kind, string account, string source, decimal amount, string method, string reason, string operationId)
    {
        ValidateAccountKind(kind);
        operationId = CommercialLedger.OperationId(operationId);
        var tender = TenderMethodCatalog.Parse(method);
        if (amount <= 0 || CommercialLedger.Money(amount) != amount || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new InvalidOperationException("Indique importe positivo de hasta dos decimales y motivo (hasta 500 caracteres).");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle);
        if (tender != TenderKind.Cash) await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        await CommandIdentity.BindAsync(db, actor, "CREDIT_PREP:" + operationId, "CREDIT_REFUND_PREPARE", kind, account, source, amount, tender, reason.Trim());
        if (await db.CreditRefundRequests.AnyAsync(r => r.OperationId == operationId)) return;
        await ValidateCreditAsync(db, kind, account, source, null, amount, true, null);
        db.CreditRefundRequests.Add(new CreditRefundRequestEntity
        {
            OperationId = operationId,
            AccountKind = kind,
            AccountId = account,
            SourceDocumentId = source,
            Amount = amount,
            TenderKind = tender,
            Reason = reason.Trim(),
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId!
        });
        CommercialLedger.Journal(db, actor, "CREDIT_PREP:" + operationId, "CREDIT_REFUND_PREPARED", operationId, amount,
            "Saldo reservado. Fondos aún no confirmados; no repetir la entrega tras una interrupción.");
        if (await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle) != actor ||
            tender != TenderKind.Cash && await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Consulte la solicitud con su sesión actual antes de mover dinero.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task ResolveRefundAsync(string requestId, bool fundsMoved, string? reference, string reason, bool evidenceVerified)
    {
        if (!evidenceVerified || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new InvalidOperationException("Verifique si el dinero fue entregado/recibido y documente la evidencia (hasta 500 caracteres).");
        reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim().ToUpperInvariant();
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var approver = await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false);
        var request = await db.CreditRefundRequests.SingleOrDefaultAsync(r => r.OperationId == requestId)
            ?? throw new InvalidOperationException("Solicitud de reembolso no encontrada.");
        ValidateAccountKind(request.AccountKind);
        if (request.Amount <= 0 || CommercialLedger.Money(request.Amount) != request.Amount ||
            request.TenderKind is not (TenderKind.Cash or TenderKind.Card or TenderKind.Transfer))
            throw new InvalidOperationException("La solicitud histórica es inconsistente; requiere revisión técnica. No mueva dinero.");
        if (request.RegisterId != approver.RegisterId) throw new UnauthorizedAccessException("Resuelva el reembolso desde su caja original.");
        if (request.CashierUserId != approver.UserId) await operations.RequireAsync(db, PermissionCatalog.CheckoutRecover, false);
        if (request.TenderKind == TenderKind.Cash || !fundsMoved) reference = null;
        else
        {
            await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
            if (reference is null || reference.Length > 200) throw new InvalidOperationException("Indique la referencia bancaria única confirmada (hasta 200 caracteres).");
        }
        var resolved = await db.CreditRefundResolutions.SingleOrDefaultAsync(r => r.RequestId == requestId);
        if (resolved is not null)
        {
            if (resolved.FundsMoved != fundsMoved || resolved.ExternalReference != reference)
                throw new InvalidOperationException("La solicitud ya tiene otra resolución. No vuelva a mover dinero.");
            if (await PendingRequests(db).AnyAsync(r => r.OperationId == requestId))
                throw new InvalidOperationException("La resolución no coincide con los registros financieros. Requiere revisión técnica; no vuelva a mover dinero.");
            await RevalidateRefundApproverAsync(db, request, approver, fundsMoved);
            return;
        }
        if (await db.AccountCreditSettlements.AnyAsync(s => s.OperationId == requestId) ||
            await db.AccountEntries.AnyAsync(s => s.OperationId == requestId) ||
            await db.ShiftMovements.AnyAsync(s => s.OperationId == requestId))
            throw new InvalidOperationException("La solicitud tiene registros financieros sin resolución coherente. Requiere revisión técnica; no vuelva a mover dinero.");
        var shift = await db.Shifts.SingleOrDefaultAsync(s => s.Id == request.ShiftId && s.IsOpen == 1);
        if (shift is null || shift.RegisterId != request.RegisterId || shift.CashierUserId != request.CashierUserId)
            throw new InvalidOperationException("La atribución del turno original es inválida; requiere conciliación antes de resolver.");
        if (fundsMoved)
        {
            var original = new OperationIdentity(request.CashierUserId, request.RegisterId, request.ShiftId);
            await PostSettlementAsync(db, original, request.AccountKind, request.AccountId, request.SourceDocumentId,
                null, request.Amount, request.Reason, request.OperationId, request.TenderKind, reference, request.OperationId);
        }
        db.CreditRefundResolutions.Add(new CreditRefundResolutionEntity
        {
            RequestId = requestId,
            FundsMoved = fundsMoved,
            ExternalReference = reference,
            Reason = reason.Trim(),
            ApproverUserId = approver.UserId
        });
        CommercialLedger.Journal(db, approver with { ShiftId = request.ShiftId }, "CREDIT_RESOLVE:" + requestId,
            fundsMoved ? "CREDIT_REFUND_CONFIRMED" : "CREDIT_REFUND_NOT_EXECUTED", requestId, request.Amount,
            reason.Trim());
        await RevalidateRefundApproverAsync(db, request, approver, fundsMoved);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task RevalidateRefundApproverAsync(AppDbContext db, CreditRefundRequestEntity request, OperationIdentity approver, bool fundsMoved)
    {
        if (await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle, false) != approver ||
            request.CashierUserId != approver.UserId && await operations.RequireAsync(db, PermissionCatalog.CheckoutRecover, false) != approver ||
            fundsMoved && request.TenderKind != TenderKind.Cash && await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false) != approver)
            throw new UnauthorizedAccessException("La sesión cambió. No vuelva a mover dinero; consulte la solicitud con su sesión actual.");
    }
}
