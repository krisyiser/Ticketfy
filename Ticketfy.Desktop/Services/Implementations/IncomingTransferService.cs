using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class IncomingTransferService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public const string SalePurpose = "CAPTURE";
    public const string CustomerPurpose = "CUSTOMER_PAYMENT";
    public const string WalletPurpose = "WALLET_LOAD";
    internal const string CancelEvent = "INCOMING_TRANSFER_CANCELLED";

    public async Task PrepareAsync(string operationId, string purpose, string destinationId, decimal amount, string notes)
    {
        CommercialLedger.OperationId(operationId);
        if (purpose is not (CustomerPurpose or WalletPurpose) || amount <= 0 || amount != CommercialLedger.Money(amount) || notes.Length > 1000)
            throw new InvalidOperationException("Destino, concepto o importe de transferencia inválido.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        await operations.RequireAsync(db, purpose == CustomerPurpose ? PermissionCatalog.CashInOut : PermissionCatalog.WalletManage);
        await CommandIdentity.BindAsync(db, actor, "TRANSFER_PREPARE:" + CommandIdentity.Hash(operationId), "TRANSFER_INTENT", operationId, purpose, destinationId, amount, notes);
        if (await db.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId)) return;
        if (await db.CommandReceipts.AnyAsync(r => r.OperationId == operationId) || await db.CheckoutOperations.AnyAsync(c => c.OperationId == operationId)
            || await db.TenderConfirmations.AnyAsync(c => c.OperationId == operationId))
            throw new InvalidOperationException("La clave ya pertenece a otra operación.");
        if (purpose == CustomerPurpose)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == destinationId && c.IsActive);
            if (customer is null || customer.Debt < amount) throw new InvalidOperationException("El abono excede la deuda o el cliente no está activo.");
        }
        else if (!await db.Giftcards.AnyAsync(g => g.Id == destinationId && g.IsActive)) throw new InvalidOperationException("Monedero inexistente o inactivo.");
        db.IncomingTransferIntents.Add(new IncomingTransferIntentEntity
        {
            OperationId = operationId,
            Purpose = purpose,
            DestinationId = destinationId,
            Amount = amount,
            Notes = notes,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId!
        });
        CommercialLedger.Journal(db, actor, operationId, "INCOMING_TRANSFER_PREPARED", destinationId, amount, purpose);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    internal static void RequireOwner(IncomingTransferIntentEntity intent, OperationIdentity actor)
    {
        if (intent.CashierUserId != actor.UserId || intent.RegisterId != actor.RegisterId || intent.ShiftId != actor.ShiftId)
            throw new UnauthorizedAccessException("La transferencia pertenece a otro usuario, caja o turno.");
    }
    internal static async Task RequireNotCancelledAsync(AppDbContext db, string operationId)
    {
        if (await db.TransactionJournal.AnyAsync(j => j.OperationId == operationId && j.EventType == CancelEvent))
            throw new InvalidOperationException("El intento fue descartado sin recepción de fondos; no puede utilizarse.");
    }
    public async Task ConfirmAndApplyAsync(string operationId, string reference, bool verified)
    {
        if (!verified) throw new InvalidOperationException("Compruebe el depósito en la cuenta del comercio, no sólo el comprobante del cliente.");
        IncomingTransferIntentEntity intent;
        string approverId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            intent = await db.IncomingTransferIntents.AsNoTracking().SingleAsync(i => i.OperationId == operationId);
            var authorization = await AuthorizeResolutionAsync(db, operations, intent);
            approverId = authorization.Approver.UserId;
            await operations.RequireAsync(db, intent.Purpose == CustomerPurpose ? PermissionCatalog.CashInOut : PermissionCatalog.WalletManage, false);
            if (await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId && r.Outcome == TransferOutcome.RefundedExternally))
                throw new InvalidOperationException("El depósito ya fue devuelto externamente; no puede aplicarse.");
        }
        // The confirmation is durable BEFORE applying the account movement. A failure leaves a recoverable pending intent.
        await new CommercialReconciliationService(factory, operations).ConfirmTransferAsync(operationId, intent.Amount, reference, intent.Purpose);
        await ApplyConfirmedTransferAsync(operationId, approverId);
    }
    public async Task CancelUnfundedAsync(string operationId, string reason, bool noFundsVerified)
    {
        if (!noFundsVerified || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new InvalidOperationException("Verifique que no se recibió dinero e indique el motivo (máximo 1000 caracteres).");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var intent = await db.IncomingTransferIntents.SingleAsync(i => i.OperationId == operationId);
        var (actor, approver) = await AuthorizeResolutionAsync(db, operations, intent);
        await CommandIdentity.BindAsync(db, actor, "TRANSFER_CANCEL:" + CommandIdentity.Hash(operationId), CancelEvent, operationId, reason);
        var cancelled = await db.TransactionJournal.AsNoTracking().SingleOrDefaultAsync(j => j.OperationId == operationId && j.EventType == CancelEvent);
        if (cancelled is not null)
        {
            if (cancelled.RegisterId != intent.RegisterId || cancelled.ShiftId != intent.ShiftId ||
                cancelled.EntityId != intent.DestinationId || cancelled.Amount != intent.Amount)
                throw new InvalidOperationException("El descarte previo no coincide con la intención original. Conserve la evidencia y solicite revisión.");
            if (await db.TenderConfirmations.AnyAsync(c => c.OperationId == operationId) ||
                await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId))
                throw new InvalidOperationException("El descarte contradice evidencia bancaria o un comprobante. Requiere revisión; no vuelva a mover dinero.");
            return;
        }
        await RequireOriginalShiftAsync(db, intent);
        if (await db.TenderConfirmations.AnyAsync(c => c.OperationId == operationId) || await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId))
            throw new InvalidOperationException("Hay fondos confirmados. Aplique el depósito o documente su devolución bancaria; no lo descarte.");
        CommercialLedger.Journal(db, approver, operationId, CancelEvent, intent.DestinationId, intent.Amount, reason);
        await RevalidateResolutionAsync(db, operations, intent, approver.UserId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task ResolveByExternalRefundAsync(string operationId, string refundReference, string reason, bool verified)
    {
        if (!verified || string.IsNullOrWhiteSpace(refundReference) || refundReference.Length > 200 || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new InvalidOperationException("Verifique el reembolso bancario y documente su referencia y motivo.");
        refundReference = refundReference.Trim();
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var intent = await db.IncomingTransferIntents.SingleAsync(i => i.OperationId == operationId);
        var (actor, approver) = await AuthorizeResolutionAsync(db, operations, intent);
        await CommandIdentity.BindAsync(db, actor, "TRANSFER_REFUND:" + CommandIdentity.Hash(operationId), "TRANSFER_REFUND", operationId, refundReference, reason);
        var existing = await db.IncomingTransferReceipts.FindAsync(operationId);
        if (existing is not null)
        {
            RequireMatchingReceipt(intent, existing);
            if (existing.Outcome != TransferOutcome.RefundedExternally || existing.RefundReference != refundReference)
                throw new InvalidOperationException("El depósito ya se aplicó o tiene otra resolución.");
            return;
        }
        await RequireOriginalShiftAsync(db, intent);
        var proof = await RequireProofAsync(db, actor, operationId, intent.Amount, intent.Purpose, intent.DestinationId);
        await BankReferenceGuard.RequireUnusedAsync(db, refundReference);
        db.IncomingTransferReceipts.Add(Receipt(actor, proof, operationId, TransferOutcome.RefundedExternally, refundReference));
        CommercialLedger.Journal(db, approver, operationId, "INCOMING_TRANSFER_REFUNDED_EXTERNALLY", intent.DestinationId, intent.Amount, reason);
        await RevalidateResolutionAsync(db, operations, intent, approver.UserId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    internal static async Task<TenderConfirmationEntity> RequireProofAsync(AppDbContext db, OperationIdentity actor, string operationId,
        decimal amount, string purpose, string? destinationId = null)
    {
        var proof = await db.TenderConfirmations.AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == operationId);
        if (proof is null || proof.Kind != TenderKind.Transfer || proof.Amount != amount || proof.Purpose != purpose
            || proof.ShiftId != actor.ShiftId || proof.RegisterId != actor.RegisterId || proof.CashierUserId != actor.UserId)
            throw new InvalidOperationException("Falta una confirmación de transferencia válida para este importe, destino y operador.");
        if (purpose != SalePurpose)
        {
            var intent = await db.IncomingTransferIntents.AsNoTracking().SingleAsync(i => i.OperationId == operationId); RequireOwner(intent, actor);
            if (intent.Amount != amount || intent.Purpose != purpose || intent.DestinationId != destinationId)
                throw new InvalidOperationException("La transferencia fue preparada para otra operación.");
            await RequireNotCancelledAsync(db, operationId);
            if (await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId && r.Outcome == TransferOutcome.RefundedExternally))
                throw new InvalidOperationException("El depósito ya fue devuelto; no puede aplicarse de nuevo.");
        }
        return proof;
    }
    internal static IncomingTransferReceiptEntity Receipt(OperationIdentity actor, TenderConfirmationEntity proof, string documentId,
        TransferOutcome outcome = TransferOutcome.Applied, string? refundReference = null) => new()
        {
            OperationId = proof.OperationId,
            Purpose = proof.Purpose,
            DocumentId = documentId,
            Amount = proof.Amount,
            ExternalReference = proof.ExternalReference,
            Outcome = outcome,
            RefundReference = refundReference,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId!
        };
    internal static IQueryable<IncomingTransferIntentEntity> PendingIntents(AppDbContext db) =>
        db.IncomingTransferIntents.Where(i =>
            !db.IncomingTransferReceipts.Any(r => r.OperationId == i.OperationId && r.Amount == i.Amount &&
                r.Purpose == i.Purpose && r.CashierUserId == i.CashierUserId && r.RegisterId == i.RegisterId && r.ShiftId == i.ShiftId &&
                (r.Outcome == TransferOutcome.Applied || r.Outcome == TransferOutcome.RefundedExternally) &&
                db.TenderConfirmations.Any(p => p.OperationId == i.OperationId && p.Kind == TenderKind.Transfer &&
                    p.Amount == i.Amount && p.Purpose == i.Purpose && p.CashierUserId == i.CashierUserId &&
                    p.ShiftId == i.ShiftId && p.RegisterId == i.RegisterId && p.ExternalReference == r.ExternalReference) &&
                !db.TransactionJournal.Any(j => j.OperationId == i.OperationId && j.EventType == CancelEvent)) &&
            !(db.TransactionJournal.Any(j => j.OperationId == i.OperationId && j.EventType == CancelEvent &&
                j.Amount == i.Amount && j.EntityId == i.DestinationId && j.RegisterId == i.RegisterId && j.ShiftId == i.ShiftId) &&
                !db.TenderConfirmations.Any(p => p.OperationId == i.OperationId) &&
                !db.IncomingTransferReceipts.Any(r => r.OperationId == i.OperationId)));
    internal static Task<bool> HasPendingAsync(AppDbContext db, string shiftId) => PendingIntents(db).AnyAsync(i => i.ShiftId == shiftId);
    public async Task<List<PendingTransferDto>> GetPendingAsync()
    {
        var result = await GetPendingPageAsync(LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(result.Next is not null);
        return result.Items;
    }
    public async Task<List<IncomingTransferReceiptEntity>> GetReceiptsAsync()
    {
        var result = await GetReceiptsPageAsync(LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(result.Next is not null);
        return result.Items;
    }
}
