using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class MercadoPagoTerminalService
{
    private async Task<OperationIdentity> RequireCheckoutAsync(string reference, decimal amount, bool recovery)
    {
        await using var db = await _contextFactory!.CreateDbContextAsync();
        return await RequireCheckoutAsync(db, reference, amount, recovery);
    }
    private async Task<OperationIdentity> RequireCheckoutAsync(AppDbContext db, string reference, decimal amount, bool recovery)
    {
        var actor = await _operations!.RequireAsync(db, recovery ? PermissionCatalog.CheckoutRecover : PermissionCatalog.PosCheckout, !recovery);
        if (recovery) await _operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        var checkout = await CheckoutDocumentQuery.Bounded(db).AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == reference)
            ?? throw new InvalidOperationException("Prepare primero la intención de venta.");
        if (checkout.RegisterId != actor.RegisterId || (!recovery && (checkout.ShiftId != actor.ShiftId || checkout.CashierUserId != actor.UserId)))
            throw new UnauthorizedAccessException("El cobro pertenece a otra caja, turno u operador.");
        if (!recovery && checkout.State is not (CheckoutOperationState.Started or CheckoutOperationState.PaymentAuthorized))
            throw new InvalidOperationException(CheckoutStatePolicy.ReconciliationRequired);
        if (!await db.Shifts.AnyAsync(s => s.Id == checkout.ShiftId && s.RegisterId == checkout.RegisterId &&
            s.CashierUserId == checkout.CashierUserId && s.IsOpen == 1))
            throw new InvalidOperationException("El turno original debe permanecer abierto.");
        var sale = CommercialDocumentLimits.ReadSale(checkout.SalePayload);
        if (checkout.FiscalPayload == "") throw new InvalidOperationException(CommercialDocumentLimits.HistoricalMessage);
        if (sale.OperationId != reference || sale.RegisterId != checkout.RegisterId ||
            sale.CashierUserId != checkout.CashierUserId || sale.ShiftId != checkout.ShiftId || sale.Total != checkout.Amount ||
            SaleService.BuildPayments(sale).Where(p => p.Kind == TenderKind.Card).Sum(p => p.Amount) != amount || amount <= 0)
            throw new InvalidOperationException("El importe de tarjeta no coincide con la intención original.");
        return actor with { ShiftId = checkout.ShiftId };
    }
    private async Task<(PaymentTerminalOperationEntity Operation, bool Created)> GetOrCreateAsync(
        string reference, string terminal, decimal amount, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory!.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireCheckoutAsync(db, reference, amount, recovery: false);
        var existing = await db.PaymentTerminalOperations.SingleOrDefaultAsync(o => o.ExternalReference == reference, cancellationToken);
        if (existing is not null)
        {
            if (existing.Amount != amount || existing.TerminalId != terminal)
                throw new InvalidOperationException("La referencia pertenece a otro importe o terminal.");
            return (existing, false);
        }
        string now = DateTimeOffset.UtcNow.ToString("O");
        var operation = new PaymentTerminalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ExternalReference = reference,
            IdempotencyKey = Guid.NewGuid().ToString(),
            TerminalId = terminal,
            Amount = amount,
            Status = IntegrationOperationStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AttemptCount = 1
        };
        db.PaymentTerminalOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return (operation, true);
    }
    private async Task<PaymentTerminalOperationEntity?> FindOperationAsync(string reference, CancellationToken cancellationToken)
    {
        if (_contextFactory is null) return null;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(o => o.ExternalReference == reference, cancellationToken);
    }
    private async Task<PaymentTerminalOperationEntity> ApplyOrderAsync(PaymentTerminalOperationEntity expected,
        MercadoPagoOrderResponse? response, bool recovery, string? requestedOrderId, CancellationToken cancellationToken,
        string? approvedUserId = null)
    {
        var evidence = TerminalOrderEvidence.Parse(response, expected, requestedOrderId);
        await using var db = await _contextFactory!.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await RequireCheckoutAsync(db, expected.ExternalReference, expected.Amount, recovery);
        if (approvedUserId is not null && actor.UserId != approvedUserId)
            throw new UnauthorizedAccessException("La sesión cambió durante la consulta.");
        var current = await db.PaymentTerminalOperations.SingleAsync(o => o.Id == expected.Id, cancellationToken);
        // Revalidate against the latest row, not the detached copy loaded before HTTP.
        evidence = TerminalOrderEvidence.Parse(response, current, requestedOrderId);
        bool conflict = false;
        if (current.ProviderUpdatedAtUtc is not null)
        {
            var previousTime = DateTimeOffset.Parse(current.ProviderUpdatedAtUtc, CultureInfo.InvariantCulture);
            if (evidence.UpdatedAt < previousTime) throw new InvalidDataException("La respuesta es anterior a la evidencia guardada.");
            if (evidence.UpdatedAt == previousTime)
            {
                if (evidence.Hash == current.ProviderEvidenceHash) return current;
                conflict = true;
            }
        }
        bool reversed = current.LastErrorCode == "funds_reversed" || current.ProviderStatus == "refunded" || response!.Status == "refunded" ||
            response.StatusDetail == "partially_refunded" ||
            decimal.Parse(response.Transactions!.Payments[0].RefundedAmount ?? "0", CultureInfo.InvariantCulture) > 0;
        bool hadFunds = current.FundsObserved || current.Status == IntegrationOperationStatus.Completed;
        var next = evidence.Status;
        if (conflict || reversed || hadFunds && next != IntegrationOperationStatus.Completed)
            next = IntegrationOperationStatus.NeedsReconciliation;
        current.FundsObserved = hadFunds || evidence.FundsObserved;
        current.ProviderOrderId = evidence.OrderId; current.ProviderPaymentId = evidence.PaymentId;
        current.ProviderStatus = evidence.ProviderStatus;
        current.Status = next;
        current.NoFundsConfirmed = !current.FundsObserved &&
            next is IntegrationOperationStatus.Cancelled or IntegrationOperationStatus.FailedPermanent;
        current.ProviderUpdatedAtUtc = evidence.UpdatedAt.ToUniversalTime().ToString("O");
        current.ProviderEvidenceHash = evidence.Hash;
        current.AuthorizationCode = evidence.AuthorizationCode; current.CardBrand = evidence.CardBrand; current.LastFourDigits = evidence.LastFourDigits;
        current.LastErrorCode = reversed ? "funds_reversed" : conflict ? "evidence_conflict" : null;
        current.LastErrorMessage = next == IntegrationOperationStatus.NeedsReconciliation ? TerminalOrderEvidence.Uncertain : null;
        current.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        CommercialLedger.Journal(db, actor, "BANKOBS:" + Guid.NewGuid().ToString("N"), "TERMINAL_ORDER_VERIFIED",
            current.ExternalReference, current.Amount,
            $"Orden {evidence.OrderId}; pago {evidence.PaymentId}; proveedor {evidence.ProviderStatus}; estado {next}; " +
            $"fecha proveedor {current.ProviderUpdatedAtUtc}; huella {evidence.Hash}; " +
            $"fondos observados {current.FundsObserved}; ausencia confirmada {current.NoFundsConfirmed}.");
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return current;
    }
    private async Task RecordTransportFailureAsync(string id, bool rejected, string code)
    {
        try
        {
            await using var db = await _contextFactory!.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            var operation = await db.PaymentTerminalOperations.SingleAsync(o => o.Id == id);
            // A failed/late HTTP response cannot overwrite durable provider evidence.
            if (operation.ProviderUpdatedAtUtc is not null || operation.FundsObserved ||
                operation.Status is IntegrationOperationStatus.Completed or IntegrationOperationStatus.Cancelled or IntegrationOperationStatus.FailedPermanent) return;
            operation.Status = rejected ? IntegrationOperationStatus.FailedPermanent : IntegrationOperationStatus.NeedsReconciliation;
            operation.NoFundsConfirmed = rejected;
            operation.LastErrorCode = code;
            operation.LastErrorMessage = TerminalOrderEvidence.Uncertain;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        catch (MaintenanceWriteBlockedException) { /* The persisted intent remains pending; no provider write is repeated. */ }
    }
}
