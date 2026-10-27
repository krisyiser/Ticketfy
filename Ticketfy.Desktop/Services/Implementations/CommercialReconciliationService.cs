using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed partial class CommercialReconciliationService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public async Task ConfirmTransferAsync(string operationId, decimal amount, string reference, string purpose = IncomingTransferService.SalePurpose)
    {
        operationId = CommercialLedger.OperationId(operationId);
        if (amount <= 0 || CommercialLedger.Money(amount) != amount || string.IsNullOrWhiteSpace(reference) || reference.Length > 200
            || purpose is not (IncomingTransferService.SalePurpose or IncomingTransferService.CustomerPurpose or IncomingTransferService.WalletPurpose))
            throw new InvalidOperationException("Indique importe y referencia bancaria comprobada.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        OperationIdentity actor, approver;
        IncomingTransferIntentEntity? intent = null;
        if (purpose != IncomingTransferService.SalePurpose)
        {
            intent = await db.IncomingTransferIntents.AsNoTracking().SingleOrDefaultAsync(i => i.OperationId == operationId)
                ?? throw new InvalidOperationException("Prepare primero el abono o recarga con su destinatario e importe.");
            (actor, approver) = await IncomingTransferService.AuthorizeResolutionAsync(db, operations, intent);
            await IncomingTransferService.RequireNotCancelledAsync(db, operationId);
            if (intent.Amount != amount || intent.Purpose != purpose) throw new InvalidOperationException("La confirmación no corresponde al intento preparado.");
        }
        else
        {
            actor = approver = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
            if (await db.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
                throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        }
        var prior = await db.TenderConfirmations.FindAsync(operationId);
        if (prior is not null)
        {
            if (prior.Kind != TenderKind.Transfer || prior.Amount != amount || !prior.ExternalReference.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase) || prior.ShiftId != actor.ShiftId
                || prior.RegisterId != actor.RegisterId || prior.CashierUserId != actor.UserId || prior.Purpose != purpose)
                throw new InvalidOperationException("La confirmación pertenece a otra operación.");
            return;
        }
        if (intent is not null) await IncomingTransferService.RequireOriginalShiftAsync(db, intent);
        await BankReferenceGuard.RequireUnusedAsync(db, reference);
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
        CommercialLedger.Journal(db, approver, operationId, "TRANSFER_VERIFIED_MANUALLY", operationId, amount, "Referencia bancaria verificada por operador autorizado");
        if (intent is not null)
            await IncomingTransferService.RevalidateResolutionAsync(db, operations, intent, approver.UserId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task SettleRefundAsync(string refundId, string reference, string operationId)
    {
        operationId = CommercialLedger.OperationId(operationId);
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200) throw new InvalidOperationException("Indique la referencia del reembolso confirmado (máximo 200 caracteres).");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        string normalizedReference = reference.Trim().ToUpperInvariant();
        var refund = await db.PaymentRefunds.AsNoTracking().SingleOrDefaultAsync(r => r.Id == refundId)
            ?? throw new InvalidOperationException("Reembolso no encontrado.");
        if (refund.Kind is not (TenderKind.Card or TenderKind.Transfer) || refund.State != TenderState.RequiresReconciliation ||
            refund.Amount <= 0 || CommercialLedger.Money(refund.Amount) != refund.Amount)
            throw new InvalidOperationException("El reembolso no admite una conciliación bancaria. Revise su estado e importe.");
        var prior = await db.RefundSettlements.FindAsync(refundId);
        if (prior is not null)
        {
            if (!prior.ExternalReference.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El reembolso ya está conciliado con otra referencia.");
            if (await PendingBankRefunds(db).AnyAsync(r => r.Id == refundId))
                throw new InvalidOperationException("La conciliación histórica no coincide con el reembolso. Requiere revisión técnica; no vuelva a mover dinero.");
            if (await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile) != actor)
                throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
            return;
        }
        await CommandIdentity.BindAsync(db, actor, operationId, "EXTERNAL_REFUND_SETTLEMENT", refundId, refund.Amount, normalizedReference);
        await BankReferenceGuard.RequireUnusedAsync(db, reference);
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
        if (await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<ReconciliationDto>> ReconcileAsync(DateTime day)
    {
        var page = await ReconcilePageAsync(day);
        if (page.HasMore) throw new InvalidOperationException("El día supera 100 turnos. Utilice la conciliación paginada.");
        return page.Items;
    }

    public async Task<ReconciliationPage> ReconcilePageAsync(DateTime day, int page = 0, int size = 100)
    {
        if (page < 0 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await factory.CreateDbContextAsync();
        // Keep all document reads in the same snapshot while another register may be posting.
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await operations.RequireAsync(db, PermissionCatalog.ReportsView, false);
        var period = new BusinessDateQuery(day.Date, day.Date);
        period.RegisterOn(db);
        var ids = db.Database.SqlQuery<long>($"""
            SELECT rowid AS Value FROM shifts WHERE
            ticketfy_business_ticks(CASE WHEN length(CAST(COALESCE(endTime,startTime) AS BLOB)) <= 128
                AND instr(COALESCE(endTime,startTime),char(0)) = 0 THEN COALESCE(endTime,startTime) ELSE '' END)
                BETWEEN {period.Start.Ticks} AND {period.End.Ticks}
            OR ticketfy_business_ticks(CASE WHEN length(CAST(COALESCE(endTime,startTime) AS BLOB)) <= 128
                AND instr(COALESCE(endTime,startTime),char(0)) = 0 THEN COALESCE(endTime,startTime) ELSE '' END) IS NULL
            """).TagWith("Daily reconciliation shifts");
        long count = await ids.LongCountAsync();
        var selected = await ids.OrderBy(id => id).Skip(offset).Take(size).ToListAsync();
        var result = new List<ReconciliationDto>();
        foreach (var row in selected)
        {
            var budget = new ReconciliationReadBudget();
            if (!await budget.ReserveAsync<ShiftEntity>(db, "rowid = {0}", row))
            { result.Add(UnreadableShift($"Registro de turno {row}", null, null)); continue; }
            var shift = await db.Shifts.FromSqlInterpolated($"SELECT * FROM shifts WHERE rowid = {row}").AsNoTracking().SingleAsync();
            if (!await budget.FitsShiftAsync(db, shift))
            { result.Add(UnreadableShift(shift.Id, shift.RegisterId, shift.CashierUserId)); continue; }
            var issues = new List<ReconciliationIssueDto>();
            if (!Ticketfy.Core.Helpers.BusinessTime.TryLocal(shift.EndTime ?? shift.StartTime, out _, period.Zone))
                issues.Add(new("SHIFT_DATE_INVALID", "Turno sin fecha válida: no puede atribuirse al día elegido y requiere revisión."));
            var sales = await ShiftLedgerValidator.Sales(db, shift.Id).ToListAsync();
            var payments = await ShiftLedgerValidator.Payments(db, shift.Id).ToListAsync();
            var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).ToListAsync();
            decimal refunds = 0m, pending = 0m; long pendingCount = 0;
            await foreach (var amount in db.PaymentRefunds.AsNoTracking().Where(r => r.ShiftId == shift.Id).Select(r => r.Amount).AsAsyncEnumerable()) refunds += amount;
            await foreach (var amount in PendingBankRefunds(db).Where(r => r.ShiftId == shift.Id).Select(r => r.Amount).AsAsyncEnumerable()) { pending += amount; pendingCount++; }
            decimal expected = ShiftLedgerValidator.ExpectedCash(shift, movements);
            decimal? counted = shift.ActualBalance;

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
            if (await AccountCreditSettlementService.HasPendingAsync(db, shift.Id))
                issues.Add(new("PENDING_CREDIT_REFUND", "Hay reembolsos de saldo a favor pendientes de confirmar o descartar con evidencia."));
            if (pendingCount != 0) issues.Add(new("PENDING_REFUND", "Hay reembolsos externos sin conciliación por su importe completo."));
            if (shift.ActualBalance.HasValue && counted != expected)
                issues.Add(new("CASH_DIFFERENCE", "El efectivo contado no coincide con el esperado."));
            if (shift.IsOpen == 0)
            {
                try { await ShiftLedgerValidator.ValidateCloseEvidenceAsync(db, shift, expected, payments); }
                catch (InvalidOperationException ex) { issues.Add(new("CLOSE_EVIDENCE", ex.Message)); }
            }
            result.Add(new(shift.Id, expected, counted, counted - expected, sales.Sum(s => s.Total), payments.Sum(p => p.Amount),
                refunds, pending, issues.Count == 0)
            {
                RegisterId = shift.RegisterId,
                CashierUserId = shift.CashierUserId,
                Issues = issues.AsReadOnly()
            });
        }
        if (await operations.RequireAsync(db, PermissionCatalog.ReportsView, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar la conciliación.");
        return new(result, count, (long)offset + selected.Count < count);
    }

    private static ReconciliationDto UnreadableShift(string id, string? register, string? user) =>
        new(id, null, null, null, null, null, null, null, false)
        { RegisterId = register, CashierUserId = user, Issues = [new("READ_BUDGET", "Cálculo no disponible: evidencia excesiva o texto no verificable. Conserve el turno y solicite revisión técnica; no ajuste saldos.")] };

}
