using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class IncomingTransferService
{
    internal static async Task<(OperationIdentity Original, OperationIdentity Approver)> AuthorizeResolutionAsync(
        AppDbContext db, CommercialOperationContext operations, IncomingTransferIntentEntity intent)
    {
        var approver = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        if (intent.RegisterId != approver.RegisterId)
            throw new UnauthorizedAccessException("Resuelva la transferencia desde su caja original.");
        if (intent.CashierUserId != approver.UserId)
            await operations.RequireAsync(db, PermissionCatalog.CheckoutRecover, false);
        if (intent.Purpose is not (CustomerPurpose or WalletPurpose) || intent.Amount <= 0 ||
            CommercialLedger.Money(intent.Amount) != intent.Amount || string.IsNullOrWhiteSpace(intent.CashierUserId) ||
            string.IsNullOrWhiteSpace(intent.ShiftId) || string.IsNullOrWhiteSpace(intent.DestinationId))
            throw new InvalidOperationException("La intención original es inconsistente. Conserve la evidencia y solicite revisión; no vuelva a cobrar.");
        return (new(intent.CashierUserId, intent.RegisterId, intent.ShiftId), approver with { ShiftId = intent.ShiftId });
    }

    internal static async Task RequireOriginalShiftAsync(AppDbContext db, IncomingTransferIntentEntity intent)
    {
        if (!await db.Shifts.AnyAsync(s => s.Id == intent.ShiftId && s.IsOpen == 1 &&
            s.CashierUserId == intent.CashierUserId && s.RegisterId == intent.RegisterId))
            throw new InvalidOperationException("El turno original debe estar abierto y conservar su atribución para resolver la transferencia.");
    }

    internal static async Task RevalidateResolutionAsync(AppDbContext db, CommercialOperationContext operations,
        IncomingTransferIntentEntity intent, string expectedApprover)
    {
        if ((await AuthorizeResolutionAsync(db, operations, intent)).Approver.UserId != expectedApprover)
            throw new UnauthorizedAccessException("La sesión cambió durante la resolución. Consulte el intento original.");
    }

    private static void RequireMatchingReceipt(IncomingTransferIntentEntity intent, IncomingTransferReceiptEntity receipt)
    {
        if (receipt.Amount != intent.Amount || receipt.Purpose != intent.Purpose || receipt.CashierUserId != intent.CashierUserId ||
            receipt.ShiftId != intent.ShiftId || receipt.RegisterId != intent.RegisterId)
            throw new InvalidOperationException("El comprobante previo no coincide con la transferencia original. Requiere revisión; no vuelva a cobrar.");
    }

    private async Task ApplyConfirmedTransferAsync(string operationId, string expectedApprover)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var intent = await db.IncomingTransferIntents.AsNoTracking().SingleAsync(i => i.OperationId == operationId);
        var (original, approver) = await AuthorizeResolutionAsync(db, operations, intent);
        if (approver.UserId != expectedApprover)
            throw new UnauthorizedAccessException("La sesión cambió. La confirmación del depósito se conserva; consulte la misma referencia, sin volver a cobrar.");
        string permission = intent.Purpose == CustomerPurpose ? PermissionCatalog.CashInOut : PermissionCatalog.WalletManage;
        await operations.RequireAsync(db, permission, false);
        var prior = await db.IncomingTransferReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operationId);
        if (prior is not null)
        {
            RequireMatchingReceipt(intent, prior);
            if (prior.Outcome != TransferOutcome.Applied)
                throw new InvalidOperationException("El depósito ya fue devuelto externamente; no puede aplicarse.");
            return;
        }
        await RequireOriginalShiftAsync(db, intent);
        if (intent.Purpose == CustomerPurpose)
            await CustomerService.PostCustomerPaymentAsync(db, original, intent.DestinationId, intent.Amount,
                "Transferencia", intent.Notes, operationId);
        else
            await GiftcardService.PostRechargeAsync(db, original, intent.DestinationId, intent.Amount,
                PaymentMethod.Transferencia, operationId);
        if (!db.IncomingTransferReceipts.Local.Any(r => r.OperationId == operationId))
            throw new InvalidOperationException("Existe un movimiento previo sin comprobante de aplicación coherente. Conserve la referencia y solicite revisión; no vuelva a mover dinero.");
        CommercialLedger.Journal(db, approver, "TRANSFER_APPLY:" + CommandIdentity.Hash(operationId),
            "INCOMING_TRANSFER_APPLIED", operationId, intent.Amount, "Aplicación de depósito verificado al destino original; sin nuevo cobro.");
        await RevalidateResolutionAsync(db, operations, intent, approver.UserId);
        await operations.RequireAsync(db, permission, false);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
