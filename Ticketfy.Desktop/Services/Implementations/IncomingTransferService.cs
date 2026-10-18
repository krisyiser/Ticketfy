using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed class IncomingTransferService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
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
        await using (var db = await factory.CreateDbContextAsync())
        {
            var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
            intent = await db.IncomingTransferIntents.AsNoTracking().SingleAsync(i => i.OperationId == operationId);
            RequireOwner(intent, actor);
            if (await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId && r.Outcome == TransferOutcome.RefundedExternally))
                throw new InvalidOperationException("El depósito ya fue devuelto externamente; no puede aplicarse.");
        }
        // The confirmation is durable BEFORE applying the account movement. A failure leaves a recoverable pending intent.
        await new CommercialReconciliationService(factory, operations).ConfirmTransferAsync(operationId, intent.Amount, reference, intent.Purpose);
        if (intent.Purpose == CustomerPurpose)
            await new CustomerService(factory, operations).RegisterCustomerPaymentAsync(intent.DestinationId, intent.Amount, "Transferencia", intent.Notes, operationId);
        else
            await new GiftcardService(factory, operations).RechargeAsync(intent.DestinationId, intent.Amount, PaymentMethod.Transferencia, intent.ShiftId, operationId);
    }
    public async Task CancelUnfundedAsync(string operationId, string reason, bool noFundsVerified)
    {
        if (!noFundsVerified || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new InvalidOperationException("Verifique que no se recibió dinero e indique el motivo (máximo 1000 caracteres).");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        var intent = await db.IncomingTransferIntents.SingleAsync(i => i.OperationId == operationId); RequireOwner(intent, actor);
        await CommandIdentity.BindAsync(db, actor, "TRANSFER_CANCEL:" + CommandIdentity.Hash(operationId), CancelEvent, operationId, reason);
        if (await db.TransactionJournal.AnyAsync(j => j.OperationId == operationId && j.EventType == CancelEvent)) return;
        if (await db.TenderConfirmations.AnyAsync(c => c.OperationId == operationId) || await db.IncomingTransferReceipts.AnyAsync(r => r.OperationId == operationId))
            throw new InvalidOperationException("Hay fondos confirmados. Aplique el depósito o documente su devolución bancaria; no lo descarte.");
        CommercialLedger.Journal(db, actor, operationId, CancelEvent, intent.DestinationId, intent.Amount, reason);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task ResolveByExternalRefundAsync(string operationId, string refundReference, string reason, bool verified)
    {
        if (!verified || string.IsNullOrWhiteSpace(refundReference) || refundReference.Length > 200 || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new InvalidOperationException("Verifique el reembolso bancario y documente su referencia y motivo.");
        refundReference = refundReference.Trim();
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        var intent = await db.IncomingTransferIntents.SingleAsync(i => i.OperationId == operationId); RequireOwner(intent, actor);
        await CommandIdentity.BindAsync(db, actor, "TRANSFER_REFUND:" + CommandIdentity.Hash(operationId), "TRANSFER_REFUND", operationId, refundReference, reason);
        var existing = await db.IncomingTransferReceipts.FindAsync(operationId);
        if (existing is not null)
        {
            if (existing.Outcome != TransferOutcome.RefundedExternally || existing.RefundReference != refundReference)
                throw new InvalidOperationException("El depósito ya se aplicó o tiene otra resolución.");
            return;
        }
        var proof = await RequireProofAsync(db, actor, operationId, intent.Amount, intent.Purpose, intent.DestinationId);
        string normalizedRefund = refundReference.ToUpperInvariant();
        if (proof.ExternalReference.Equals(refundReference, StringComparison.OrdinalIgnoreCase)
            || await db.RefundSettlements.AnyAsync(r => r.ExternalReference.ToUpper() == normalizedRefund)
            || await db.IncomingTransferReceipts.AnyAsync(r => r.RefundReference != null && r.RefundReference.ToUpper() == normalizedRefund))
            throw new InvalidOperationException("La referencia del reembolso no puede reutilizar otra transacción.");
        db.IncomingTransferReceipts.Add(Receipt(actor, proof, operationId, TransferOutcome.RefundedExternally, refundReference));
        CommercialLedger.Journal(db, actor, operationId, "INCOMING_TRANSFER_REFUNDED_EXTERNALLY", intent.DestinationId, intent.Amount, reason);
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
    internal static Task<bool> HasPendingAsync(AppDbContext db, string shiftId) => db.IncomingTransferIntents.AnyAsync(i => i.ShiftId == shiftId
        && !db.IncomingTransferReceipts.Any(r => r.OperationId == i.OperationId)
        && !db.TransactionJournal.Any(j => j.OperationId == i.OperationId && j.EventType == CancelEvent));
    public async Task<List<PendingTransferDto>> GetPendingAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        return await db.IncomingTransferIntents.AsNoTracking().Where(i => i.CashierUserId == actor.UserId && i.RegisterId == actor.RegisterId
            && !db.IncomingTransferReceipts.Any(r => r.OperationId == i.OperationId)
            && !db.TransactionJournal.Any(j => j.OperationId == i.OperationId && j.EventType == CancelEvent))
            .OrderBy(i => i.CreatedAtUtc).Take(200).Select(i => new PendingTransferDto(i.OperationId, i.Purpose, i.DestinationId, i.Amount,
                db.TenderConfirmations.Where(c => c.OperationId == i.OperationId).Select(c => c.ExternalReference).FirstOrDefault())).ToListAsync();
    }
    public async Task<List<IncomingTransferReceiptEntity>> GetReceiptsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await operations.RequireAsync(db, PermissionCatalog.ReportsView, false);
        return await db.IncomingTransferReceipts.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync();
    }
}
