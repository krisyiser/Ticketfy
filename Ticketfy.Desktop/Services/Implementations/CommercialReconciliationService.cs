using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed class CommercialReconciliationService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public async Task ConfirmTransferAsync(string operationId, decimal amount, string reference, string purpose = IncomingTransferService.SalePurpose)
    {
        CommercialLedger.OperationId(operationId);
        if (amount <= 0 || CommercialLedger.Money(amount) != amount || string.IsNullOrWhiteSpace(reference) || reference.Length > 200
            || purpose is not (IncomingTransferService.SalePurpose or IncomingTransferService.CustomerPurpose or IncomingTransferService.WalletPurpose))
            throw new InvalidOperationException("Indique importe y referencia bancaria comprobada.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        if (purpose != IncomingTransferService.SalePurpose)
        {
            var intent = await db.IncomingTransferIntents.AsNoTracking().SingleOrDefaultAsync(i => i.OperationId == operationId)
                ?? throw new InvalidOperationException("Prepare primero el abono o recarga con su destinatario e importe.");
            IncomingTransferService.RequireOwner(intent, actor);
            await IncomingTransferService.RequireNotCancelledAsync(db, operationId);
            if (intent.Amount != amount || intent.Purpose != purpose) throw new InvalidOperationException("La confirmación no corresponde al intento preparado.");
        }
        else if (await db.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
            throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        var prior = await db.TenderConfirmations.FindAsync(operationId);
        if (prior is not null)
        {
            if (prior.Amount != amount || !prior.ExternalReference.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase) || prior.ShiftId != actor.ShiftId
                || prior.RegisterId != actor.RegisterId || prior.CashierUserId != actor.UserId || prior.Purpose != purpose)
                throw new InvalidOperationException("La confirmación pertenece a otra operación.");
            return;
        }
        string normalizedReference = reference.Trim().ToUpperInvariant();
        if (await db.TenderConfirmations.AnyAsync(c => c.Kind == TenderKind.Transfer && c.ExternalReference.ToUpper() == normalizedReference)
            || await db.IncomingTransferReceipts.AnyAsync(r => r.ExternalReference.ToUpper() == normalizedReference))
            throw new InvalidOperationException("La referencia bancaria ya pertenece a otro depósito registrado.");
        db.TenderConfirmations.Add(new TenderConfirmationEntity
        {
            OperationId = operationId,
            Amount = amount,
            Kind = TenderKind.Transfer,
            Purpose = purpose,
            ExternalReference = reference.Trim(),
            ShiftId = actor.ShiftId!,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId
        });
        CommercialLedger.Journal(db, actor, operationId, "TRANSFER_VERIFIED_MANUALLY", operationId, amount, "Referencia bancaria verificada por operador autorizado");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task SettleRefundAsync(string refundId, string reference, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200) throw new InvalidOperationException("Indique la referencia del reembolso confirmado (máximo 200 caracteres).");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        string normalizedReference = reference.Trim().ToUpperInvariant();
        if (await db.IncomingTransferReceipts.AnyAsync(r => r.RefundReference != null && r.RefundReference.ToUpper() == normalizedReference)
            || await db.RefundSettlements.AnyAsync(r => r.RefundId != refundId && r.ExternalReference.ToUpper() == normalizedReference))
            throw new InvalidOperationException("La referencia ya concilia la devolución de otro depósito.");
        var prior = await db.RefundSettlements.FindAsync(refundId);
        if (prior is not null)
        {
            if (!prior.ExternalReference.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El reembolso ya está conciliado con otra referencia.");
            return;
        }
        var refund = await db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        if (refund.Kind is not (TenderKind.Card or TenderKind.Transfer)) throw new InvalidOperationException("Este reembolso se ejecutó localmente.");
        db.RefundSettlements.Add(new RefundSettlementEntity
        {
            RefundId = refundId,
            OperationId = operationId,
            Amount = refund.Amount,
            ExternalReference = reference.Trim(),
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId!
        });
        CommercialLedger.Journal(db, actor, operationId, "EXTERNAL_REFUND_VERIFIED", refundId, refund.Amount, "Reembolso externo confirmado por operador autorizado");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<PaymentRefundEntity>> GetPendingRefundsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        return await db.PaymentRefunds.AsNoTracking().Where(r => r.State == TenderState.RequiresReconciliation
            && !db.RefundSettlements.Any(s => s.RefundId == r.Id)).OrderBy(r => r.CreatedAtUtc).ToListAsync();
    }
    public async Task<List<ReconciliationDto>> ReconcileAsync(DateTime day)
    {
        await using var db = await factory.CreateDbContextAsync();
        // Keep all document reads in the same snapshot while another register may be posting.
        await using var snapshot = await db.Database.BeginTransactionAsync();
        await operations.RequireAsync(db, PermissionCatalog.ReportsView, false);
        var shifts = (await db.Shifts.AsNoTracking().ToListAsync()).Where(s =>
            !DateTimeOffset.TryParse(s.EndTime ?? s.StartTime, out var date) || date.LocalDateTime.Date == day.Date)
            .OrderBy(s => s.RegisterId).ThenBy(s => s.StartTime).ToList();
        var result = new List<ReconciliationDto>();
        foreach (var shift in shifts)
        {
            var issues = new List<ReconciliationIssueDto>();
            if (!DateTimeOffset.TryParse(shift.EndTime ?? shift.StartTime, out _))
                issues.Add(new("SHIFT_DATE_INVALID", "Turno sin fecha válida: no puede atribuirse al día elegido y requiere revisión."));
            var sales = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shift.Id).ToListAsync();
            var payments = await db.SalePayments.AsNoTracking().Where(p => p.ShiftId == shift.Id).ToListAsync();
            var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).ToListAsync();
            var refunds = await db.PaymentRefunds.AsNoTracking().Where(r => r.ShiftId == shift.Id).ToListAsync();
            var settledIds = (await (from settlement in db.RefundSettlements.AsNoTracking()
                                     join refund in db.PaymentRefunds.AsNoTracking() on settlement.RefundId equals refund.Id
                                     where refund.ShiftId == shift.Id && settlement.Amount == refund.Amount
                                     select settlement.RefundId).ToListAsync()).ToHashSet();
            decimal pending = refunds.Where(r => r.State == TenderState.RequiresReconciliation && !settledIds.Contains(r.Id)).Sum(r => r.Amount);
            decimal expected = ShiftLedgerValidator.ExpectedCash(shift, movements);
            decimal counted = shift.ActualBalance ?? expected;

            if (shift.IsOpen != 0) issues.Add(new("SHIFT_OPEN", "El turno sigue abierto."));
            if (!shift.ActualBalance.HasValue) issues.Add(new("COUNT_MISSING", "No existe conteo final de efectivo."));
            if (string.IsNullOrWhiteSpace(shift.RegisterId) || string.IsNullOrWhiteSpace(shift.CashierUserId))
                issues.Add(new("OWNER_MISSING", "El turno histórico no tiene caja y operador verificables."));
            try { ShiftLedgerValidator.ValidateSales(shift, sales, payments); }
            catch (InvalidOperationException ex) { issues.Add(new("PAYMENT_LEDGER", ex.Message)); }
            try { await ShiftLedgerValidator.ValidateCashAsync(db, shift, payments, movements); }
            catch (InvalidOperationException ex) { issues.Add(new("CASH_LEDGER", ex.Message)); }
            try { ShiftLedgerValidator.ValidateCashProjection(shift, expected); }
            catch (InvalidOperationException ex) { issues.Add(new("CASH_PROJECTION", ex.Message)); }
            if (await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, shift.Id))
                issues.Add(new("PENDING_CHECKOUT", "Hay cobros interrumpidos sin resolver."));
            if (await IncomingTransferService.HasPendingAsync(db, shift.Id))
                issues.Add(new("PENDING_TRANSFER", "Hay depósitos pendientes de aplicar o resolver."));
            if (pending != 0m) issues.Add(new("PENDING_REFUND", "Hay reembolsos externos sin conciliación por su importe completo."));
            if (shift.ActualBalance.HasValue && counted != expected)
                issues.Add(new("CASH_DIFFERENCE", "El efectivo contado no coincide con el esperado."));
            if (shift.IsOpen == 0)
            {
                try { await ShiftLedgerValidator.ValidateCloseEvidenceAsync(db, shift, expected, payments); }
                catch (InvalidOperationException ex) { issues.Add(new("CLOSE_EVIDENCE", ex.Message)); }
            }
            result.Add(new(shift.Id, expected, counted, counted - expected, sales.Sum(s => s.Total), payments.Sum(p => p.Amount),
                refunds.Sum(r => r.Amount), pending, issues.Count == 0)
            {
                RegisterId = shift.RegisterId,
                CashierUserId = shift.CashierUserId,
                Issues = issues.AsReadOnly()
            });
        }
        await snapshot.CommitAsync();
        return result;
    }
}
