using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public partial class SaleService
{
    private const int RecoveryBatchSize = 100;
    private const string InvalidRecoveryMessage =
        "El intento no pudo recuperarse automáticamente. Revise datos originales, fondos y permisos en Control comercial; no vuelva a cobrar.";
    private const string MissingCheckoutCompositionMessage =
        "El cobro heredado de un combo no tiene una composición verificada. Requiere revisión técnica con evidencia original; no se puede reconstruir automáticamente. No se registró otra venta ni se descontó inventario; no lo vuelva a cobrar.";
    private const string ChangedCheckoutCompositionMessage =
        "La composición del combo cambió. No se registró otra venta ni se descontó inventario. Concilie el pago y restaure la composición original antes de completar este cobro; no lo vuelva a cobrar.";

    public async Task<int> RecoverInterruptedCheckoutsAsync()
    {
        string? through;
        await using (var db = await _contextFactory.CreateDbContextAsync())
            through = await CheckoutStatePolicy.Pending(db).Where(o => o.State != CheckoutOperationState.RequiresReconciliation)
                .Select(o => o.OperationId).MaxAsync();
        if (through is null) return 0;
        string? after = null;
        int recovered = 0;
        while (true)
        {
            List<string> ids;
            await using (var db = await _contextFactory.CreateDbContextAsync())
            {
                var query = CheckoutStatePolicy.Pending(db).AsNoTracking()
                    .Where(o => o.State != CheckoutOperationState.RequiresReconciliation &&
                        string.Compare(EF.Functions.Collate(o.OperationId, "BINARY"), through) <= 0);
                if (after is not null)
                    query = query.Where(o => string.Compare(EF.Functions.Collate(o.OperationId, "BINARY"), after) > 0);
                ids = await query.OrderBy(o => EF.Functions.Collate(o.OperationId, "BINARY"))
                    .Select(o => o.OperationId).Take(RecoveryBatchSize).ToListAsync();
            }
            foreach (string id in ids)
            {
                try
                {
                    var sale = await PrepareStartupRecoveryAsync(id);
                    if (sale is null) continue;
                    await SaveCoreAsync(sale, recovering: true);
                    recovered++;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException
                    or OverflowException or FormatException or UnauthorizedAccessException or DbUpdateException
                    or System.Data.Common.DbException)
                {
                    // A damaged document is isolated; a storage failure that prevents recording
                    // the unresolved state still propagates and stops database initialization.
                    Serilog.Log.Warning("Checkout {OperationId} requires reconciliation after {FailureType}", id, ex.GetType().Name);
                    // Only these exact, application-owned messages are safe to persist.
                    // Other exceptions can include customer, product, JSON or storage data.
                    string reason = ex is InvalidOperationException &&
                        ex.Message is MissingCheckoutCompositionMessage or ChangedCheckoutCompositionMessage
                            ? ex.Message : InvalidRecoveryMessage;
                    await MarkCheckoutFailedCoreAsync(id, reason, requiresReconciliation: true);
                }
            }
            if (ids.Count < RecoveryBatchSize) return recovered;
            after = ids[^1];
            await Task.Yield();
        }
    }

    private async Task<SaleDto?> PrepareStartupRecoveryAsync(string operationId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // The batch contains keys only. Re-read under the writer reservation before
        // changing state, so another checkout's committed result cannot be downgraded.
        var operation = await CheckoutDocumentQuery.Bounded(db).SingleOrDefaultAsync(o => o.OperationId == operationId &&
            CheckoutStatePolicy.Pending(db).Any(p => p.OperationId == o.OperationId));
        if (operation is null || operation.State == CheckoutOperationState.RequiresReconciliation) return null;
        if (await db.Sales.AnyAsync(s => s.OperationId == operationId))
        {
            await UpdateCheckoutFailureAsync(db, operationId, "", false);
            await tx.CommitAsync();
            return null;
        }
        if (operation.State == CheckoutOperationState.Failed || !Enum.IsDefined(operation.State))
        {
            await UpdateCheckoutFailureAsync(db, operationId, InvalidRecoveryMessage, true);
            await tx.CommitAsync();
            return null;
        }
        var sale = CommercialDocumentLimits.ReadSale(operation.SalePayload);
        if (!HasCheckoutPayloadStructure(sale) || sale.OperationId != operationId || sale.Total != operation.Amount ||
            sale.RegisterId != operation.RegisterId || sale.ShiftId != operation.ShiftId || sale.CashierUserId != operation.CashierUserId)
            throw new InvalidOperationException(InvalidRecoveryMessage);
        bool cash = BuildPayments(sale).Any(p => p.Kind == TenderKind.Cash);
        if (operation.State == CheckoutOperationState.Started)
        {
            var terminal = await db.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(t => t.ExternalReference == operationId);
            var transfer = await db.TenderConfirmations.AsNoTracking().SingleOrDefaultAsync(t => t.OperationId == operationId);
            if (!TerminalOrderEvidence.IsCaptured(terminal) && transfer is null)
            {
                await UpdateCheckoutFailureAsync(db, operationId, InvalidRecoveryMessage,
                    cash || terminal is not null && !TerminalOrderEvidence.NoFunds(terminal));
                await tx.CommitAsync();
                return null;
            }
            operation.State = CheckoutOperationState.PaymentAuthorized;
            operation.PaymentReference = TerminalOrderEvidence.IsCaptured(terminal) ? terminal!.ProviderPaymentId : transfer!.ExternalReference;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        }
        if (cash && !await db.TransactionJournal.AnyAsync(j => j.OperationId == "RESOLVE:" + operationId && j.EventType == "CHECKOUT_FUNDS_VERIFIED"))
            throw new InvalidOperationException(InvalidRecoveryMessage);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return sale;
    }
}
