using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Shared invariants for closing a register and subsequently reconciling its documents.</summary>
internal static class ShiftLedgerValidator
{
    internal static IQueryable<SaleEntity> Sales(AppDbContext db, string shiftId) =>
        db.Sales.AsNoTracking().Where(s => s.ShiftId == shiftId).Select(s => new SaleEntity
        {
            Id = s.Id,
            OperationId = s.OperationId,
            LedgerVersion = s.LedgerVersion,
            Total = s.Total,
            ShiftId = s.ShiftId,
            RegisterId = s.RegisterId,
            CashierUserId = s.CashierUserId
        });
    internal static IQueryable<SalePaymentEntity> Payments(AppDbContext db, string shiftId) =>
        db.SalePayments.AsNoTracking().Where(p => p.ShiftId == shiftId).Select(p => new SalePaymentEntity
        {
            Id = p.Id,
            SaleId = p.SaleId,
            OperationId = p.OperationId,
            Kind = p.Kind,
            State = p.State,
            Amount = p.Amount,
            ShiftId = p.ShiftId,
            RegisterId = p.RegisterId,
            CashierUserId = p.CashierUserId
        });
    public static void ValidateSales(ShiftEntity shift, IReadOnlyList<SaleEntity> sales, IReadOnlyList<SalePaymentEntity> payments)
    {
        var salesById = sales.ToDictionary(s => s.Id);
        var paymentsBySale = payments.ToLookup(p => p.SaleId);
        bool invalidSales = sales.Any(s => s.LedgerVersion == 0 || s.Total < 0m || string.IsNullOrWhiteSpace(s.OperationId)
            || s.ShiftId != shift.Id || s.RegisterId != shift.RegisterId || s.CashierUserId != shift.CashierUserId
            || s.Total != paymentsBySale[s.Id].Sum(p => p.Amount));
        bool invalidPayments = payments.Any(p => p.Amount <= 0m || p.State != TenderState.Captured || !Enum.IsDefined(p.Kind)
            || p.ShiftId != shift.Id || p.RegisterId != shift.RegisterId || p.CashierUserId != shift.CashierUserId
            || !salesById.TryGetValue(p.SaleId, out var sale) || p.OperationId != sale.OperationId);
        if (invalidSales || invalidPayments || sales.Sum(s => s.Total) != payments.Sum(p => p.Amount))
            throw new InvalidOperationException("Ventas y pagos difieren en importe, estado, documento u operador. Revise el turno.");
    }

    public static decimal ExpectedCash(ShiftEntity shift, IEnumerable<ShiftMovementEntity> movements) =>
        shift.OpeningBalance + movements.Sum(m => m.IsOutflow ? -m.Amount : m.Amount);

    public static void ValidateCashProjection(ShiftEntity shift, decimal expected)
    {
        if (shift.ExpectedBalance != expected)
            throw new InvalidOperationException("El saldo almacenado de caja difiere de sus movimientos. No se reparará automáticamente al cerrar.");
    }

    public static Task<bool> HasPendingCheckoutsAsync(AppDbContext db, string shiftId) =>
        CheckoutStatePolicy.Pending(db).AnyAsync(o => o.ShiftId == shiftId);

    public static async Task ValidateCloseEvidenceAsync(AppDbContext db, ShiftEntity shift, decimal expected, IReadOnlyList<SalePaymentEntity> payments)
    {
        var cashups = await db.Cashups.AsNoTracking().Where(c => c.ShiftId == shift.Id && c.Type == "Final").ToListAsync();
        var events = await db.TransactionJournal.AsNoTracking().Where(j => j.ShiftId == shift.Id && j.EventType == "SHIFT_CLOSED").ToListAsync();
        var command = await db.CommandReceipts.AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == shift.CloseOperationId);
        if (cashups.Count != 1 || events.Count != 1 || command is null || !DateTimeOffset.TryParse(shift.EndTime, out _))
            throw new InvalidOperationException("Falta un arqueo final único, comando, fecha o registro de cierre verificable.");
        var cashup = cashups[0];
        CashCountEvidence.Validate(cashup);
        var journal = events[0];
        if (cashup.OperationId != shift.CloseOperationId || cashup.RegisterId != shift.RegisterId || cashup.CashierUserId != shift.CashierUserId
            || cashup.OpenCashAmount != shift.OpeningBalance || cashup.ClosedCashAmount != shift.ActualBalance
            || cashup.TheoreticalCash != expected || cashup.Difference != shift.ActualBalance - expected || shift.Diff != cashup.Difference
            || journal.OperationId != shift.CloseOperationId || journal.EntityId != shift.Id || journal.Amount != shift.ActualBalance
            || journal.RegisterId != shift.RegisterId || journal.CashierUserId != shift.CashierUserId
            || command.RegisterId != shift.RegisterId || command.CashierUserId != shift.CashierUserId || command.ShiftId != shift.Id
            || !CashCountEvidence.Matches(command, "SHIFT_CLOSE", shift.CashierUserId, shift.RegisterId, shift.Id, cashup)
            || shift.TotalCashSales != payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount)
            || shift.TotalCreditSales != payments.Where(p => p.Kind == TenderKind.Credit).Sum(p => p.Amount))
            throw new InvalidOperationException("El arqueo, diario, comando o totales del cierre no coinciden con el turno y sus documentos.");
    }

    private sealed record CashSource(MovementType Type, bool Outflow, string Document, string? Operation,
        decimal Amount, string? Register, string? User)
    {
        public (MovementType, bool, string, string?) Key => (Type, Outflow, Document, Operation);
    }

    public static async Task ValidateCashAsync(AppDbContext db, ShiftEntity shift,
        IReadOnlyList<SalePaymentEntity> payments, IReadOnlyList<ShiftMovementEntity> movements)
    {
        var expected = new List<CashSource>();
        expected.AddRange(payments.Where(p => p.Kind == TenderKind.Cash)
            .Select(p => new CashSource(MovementType.VentaEfectivo, false, p.SaleId, p.OperationId, p.Amount, p.RegisterId, p.CashierUserId)));

        var refunds = await db.PaymentRefunds.AsNoTracking().Where(r => r.ShiftId == shift.Id && r.Kind == TenderKind.Cash).ToListAsync();
        if (refunds.Any(r => r.State != TenderState.Refunded))
            throw new InvalidOperationException("Hay una devolución de efectivo sin estado de reembolso confirmado.");
        expected.AddRange(refunds.Select(r => new CashSource(MovementType.DevolucionCliente, true, r.ReturnId, r.OperationId, r.Amount, r.RegisterId, r.CashierUserId)));

        var customerPayments = await db.CustomerPayments.AsNoTracking().Where(p => p.ShiftId == shift.Id && (p.Method == "Efectivo" || p.Method == "Cash")).ToListAsync();
        var creditRefunds = await db.AccountCreditSettlements.AsNoTracking().Where(s => s.ShiftId == shift.Id
            && s.Kind == AccountCreditSettlementKind.Refund && s.TenderKind == TenderKind.Cash).ToListAsync();
        if (creditRefunds.Any(s => s.AccountKind is not (AccountKind.Customer or AccountKind.Supplier)))
            throw new InvalidOperationException("Reembolso de saldo a favor con cuenta inválida.");
        expected.AddRange(creditRefunds.Select(s => new CashSource(
            s.AccountKind == AccountKind.Customer ? MovementType.ReembolsoSaldoCliente : MovementType.ReembolsoProveedor,
            s.AccountKind == AccountKind.Customer, s.OperationId, s.OperationId, s.Amount, s.RegisterId, s.CashierUserId)));
        if (customerPayments.Any(p => p.Status != PaymentStatus.Completed))
            throw new InvalidOperationException("Hay un abono en efectivo sin confirmar.");
        expected.AddRange(customerPayments.Select(p => new CashSource(MovementType.AbonoCliente, false, p.Id, p.OperationId, p.Amount, p.RegisterId, p.CashierUserId)));

        var supplierPayments = await db.SupplierPayments.AsNoTracking().Where(p => p.ShiftId == shift.Id && p.Kind == TenderKind.Cash).ToListAsync();
        expected.AddRange(supplierPayments.Select(p => new CashSource(MovementType.CompraEfectivo, true, p.PurchaseId, p.OperationId, p.Amount, p.RegisterId, p.CashierUserId)));

        var expenses = await db.Expenses.AsNoTracking().Where(e => e.ShiftId == shift.Id && (e.PaymentMethod == "Efectivo" || e.PaymentMethod == "Cash")).ToListAsync();
        expected.AddRange(expenses.Select(e => new CashSource(MovementType.GastoOperativo, true, e.Id, e.OperationId, e.Amount, e.RegisterId, e.RegisteredByUser)));

        var loads = await db.AccountEntries.AsNoTracking().Where(e => e.ShiftId == shift.Id && e.AccountKind == AccountKind.Giftcard && e.EntryType == "CASH_LOAD").ToListAsync();
        expected.AddRange(loads.Select(e => new CashSource(MovementType.RecargaMonedero, false, e.DocumentId, e.OperationId, e.Amount, e.RegisterId, e.CashierUserId)));

        // A refund of an expense belongs to the shift that received the cash, not necessarily the original shift.
        var cancelled = await (from journal in db.TransactionJournal.AsNoTracking()
                               join expense in db.Expenses.AsNoTracking() on journal.EntityId equals expense.Id
                               where journal.ShiftId == shift.Id && journal.EventType == "EXPENSE_CANCELLED"
                                   && (expense.PaymentMethod == "Efectivo" || expense.PaymentMethod == "Cash")
                               select new { Journal = journal, Expense = expense }).ToListAsync();
        if (cancelled.Any(c => !c.Expense.IsCancelled || c.Journal.Amount != c.Expense.Amount))
            throw new InvalidOperationException("La recuperación de un gasto no coincide con su cancelación documentada.");
        expected.AddRange(cancelled.Select(c => new CashSource(MovementType.CancelacionGasto, false, c.Expense.Id,
            c.Journal.OperationId, c.Expense.Amount, c.Journal.RegisterId, c.Journal.CashierUserId)));

        var manualEvents = await db.TransactionJournal.AsNoTracking().Where(j => j.ShiftId == shift.Id && j.EventType == "CASH_MOVEMENT").ToListAsync();
        var manualCommands = await db.CommandReceipts.AsNoTracking().Where(c => c.ShiftId == shift.Id && c.Command == "CASH_MOVEMENT").ToDictionaryAsync(c => c.OperationId);
        var movementsByOperation = movements.ToLookup(m => m.OperationId);
        foreach (var entry in manualEvents)
        {
            var matches = movementsByOperation[entry.OperationId].ToList();
            if (matches.Count != 1 || !manualCommands.TryGetValue(entry.OperationId, out var command))
                throw new InvalidOperationException("Un ingreso o retiro manual no tiene un único movimiento y comando respaldados.");
            var movement = matches[0];
            string fingerprint = CommandIdentity.Hash("CASH_MOVEMENT", entry.CashierUserId, entry.RegisterId, entry.ShiftId,
                entry.Amount, movement.IsOutflow, entry.Details);
            if (command.Fingerprint != fingerprint || command.RegisterId != shift.RegisterId || command.CashierUserId != shift.CashierUserId
                || command.ShiftId != shift.Id || entry.EntityId != entry.OperationId || entry.Details != movement.Description)
                throw new InvalidOperationException("Un ingreso o retiro difiere del comando autorizado, incluido su sentido de entrada o salida.");
            expected.Add(new CashSource(movement.IsOutflow ? MovementType.RetiroEfectivo : MovementType.IngresoExtraordinario,
                movement.IsOutflow, entry.EntityId, entry.OperationId, entry.Amount, entry.RegisterId, entry.CashierUserId));
        }

        var actual = movements.Select(m => new CashSource(m.MovementType, m.IsOutflow, m.ReferenceId, m.OperationId, m.Amount, m.RegisterId, m.CashierUserId)).ToList();
        if (expected.Concat(actual).Any(e => e.Amount <= 0m || string.IsNullOrWhiteSpace(e.Operation)
            || e.Register != shift.RegisterId || e.User != shift.CashierUserId))
            throw new InvalidOperationException("Un movimiento de efectivo o su documento carece de importe, operación o atribución válida.");

        var expectedTotals = expected.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        var actualTotals = actual.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        if (expectedTotals.Count != actualTotals.Count || expectedTotals.Any(e => !actualTotals.TryGetValue(e.Key, out var amount) || amount != e.Value))
            throw new InvalidOperationException("El ledger de efectivo no coincide con sus documentos y operaciones. Cierre bloqueado para conciliación.");
    }
}
