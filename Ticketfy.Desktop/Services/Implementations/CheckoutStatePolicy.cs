using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CheckoutStatePolicy
{
    internal const string ReconciliationRequired =
        "El cobro requiere conciliación en Control comercial, en su caja original. Conserve la referencia; no vuelva a entregar dinero ni a cobrar.";
    internal const string ClosedIntent =
        "La intención ya fue descartada o rechazada; no puede reutilizarse para registrar una venta.";

    // Include old Failed rows with contradictory bank evidence; never hide them at shift close.
    internal static IQueryable<CheckoutOperationEntity> Pending(AppDbContext db) =>
        db.CheckoutOperations.Where(o => o.State != CheckoutOperationState.Completed &&
            (o.State != CheckoutOperationState.Failed ||
                db.TenderConfirmations.Any(t => t.OperationId == o.OperationId) ||
                db.PaymentTerminalOperations.Any(t => t.ExternalReference == o.OperationId &&
                    (t.FundsObserved || !t.NoFundsConfirmed || t.Status != IntegrationOperationStatus.Cancelled && t.Status != IntegrationOperationStatus.FailedPermanent))));

    internal static async Task RequireNoOtherPendingAsync(AppDbContext db, string? shiftId, string operationId)
    {
        if (await Pending(db).AnyAsync(o => o.ShiftId == shiftId && o.OperationId != operationId))
            throw new InvalidOperationException(ReconciliationRequired);
    }

    internal static async Task<bool> HasBankEvidenceAsync(AppDbContext db, string operationId) =>
        await db.TenderConfirmations.AnyAsync(t => t.OperationId == operationId) ||
        await db.PaymentTerminalOperations.AnyAsync(t => t.ExternalReference == operationId &&
            (t.FundsObserved || !t.NoFundsConfirmed || t.Status != IntegrationOperationStatus.Cancelled && t.Status != IntegrationOperationStatus.FailedPermanent));
}
