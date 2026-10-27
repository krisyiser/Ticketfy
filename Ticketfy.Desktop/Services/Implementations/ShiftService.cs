using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed partial class ShiftService : IShiftService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ShiftService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ShiftService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    private CommercialOperationContext Operations => _operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.");
    public async Task<ShiftDto?> GetActiveAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.PosCheckout, false);
        var entity = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.IsOpen == 1 && s.RegisterId == actor.RegisterId);
        if (entity is not null && entity.CashierUserId != actor.UserId)
            await Operations.RequireAsync(db, PermissionCatalog.CashHistory, false);
        return entity is null ? null : Map(entity);
    }
    public async Task<ShiftCashSummary?> GetActiveCashSummaryAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashClose, false);
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.IsOpen == 1 && s.RegisterId == actor.RegisterId);
        if (shift is null) return null;
        if (shift.CashierUserId != actor.UserId) throw new UnauthorizedAccessException("El turno pertenece a otro operador. Inicie sesión con el responsable de la caja.");
        decimal inflows = 0m, outflows = 0m;
        await foreach (var movement in db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id)
            .Select(m => new { m.Amount, m.IsOutflow }).AsAsyncEnumerable())
        {
            if (movement.IsOutflow) outflows += movement.Amount;
            else inflows += movement.Amount;
        }
        ShiftLedgerValidator.ValidateCashProjection(shift, shift.OpeningBalance + inflows - outflows);
        return new(Map(shift), inflows, outflows);
    }
    public Task<ShiftDto> OpenAsync(decimal openingBalance) => OpenAsync(openingBalance, Guid.NewGuid().ToString("N"));
    public async Task<ShiftDto> OpenAsync(decimal openingBalance, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (openingBalance < 0 || CommercialLedger.Money(openingBalance) != openingBalance)
            throw new InvalidOperationException("Fondo inicial inválido.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashOpen, false);
        if (await db.Shifts.AnyAsync(s => s.IsOpen == 1 && (s.RegisterId == null || s.CashierUserId == null)))
            throw new InvalidOperationException("Hay un turno anterior sin atribución de caja. Concilie su cierre en Control antes de abrir otro.");
        await CommandIdentity.BindAsync(db, actor, operationId, "SHIFT_OPEN", openingBalance);
        var existing = await db.Shifts.SingleOrDefaultAsync(s => s.OpenOperationId == operationId);
        if (existing is not null)
        {
            if (existing.RegisterId != actor.RegisterId || existing.CashierUserId != actor.UserId || existing.OpeningBalance != openingBalance)
                throw new InvalidOperationException("La clave de apertura ya fue utilizada.");
            return Map(existing);
        }
        if (await db.Shifts.AnyAsync(s => s.RegisterId == actor.RegisterId && s.IsOpen == 1))
            throw new InvalidOperationException("Esta caja ya tiene un turno abierto.");
        var entity = new ShiftEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            OpenOperationId = operationId,
            StartTime = DateTimeOffset.UtcNow.ToString("O"),
            OpeningBalance = openingBalance,
            ExpectedBalance = openingBalance,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            IsOpen = 1
        };
        db.Shifts.Add(entity);
        CommercialLedger.Journal(db, actor with { ShiftId = entity.Id }, operationId, "SHIFT_OPENED", entity.Id, openingBalance, "Fondo inicial");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Map(entity);
    }
    public Task<ShiftDto> CloseAsync(string shiftId, decimal actualBalance) => CloseWithCashupAsync(shiftId,
        new CashupEntity { Id = Guid.NewGuid().ToString("N"), OperationId = $"CLOSE:{shiftId}", ClosedCashAmount = actualBalance, Type = "Final" });

    public async Task<ShiftDto> CloseWithCashupAsync(string shiftId, CashupEntity cashup)
    {
        string operation = CommercialLedger.OperationId(cashup.OperationId);
        CashCountEvidence.Validate(cashup);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashClose, false);
        var budget = new ReconciliationReadBudget();
        if (!await budget.ReserveAsync<ShiftEntity>(db, "id = {0}", shiftId))
            throw new InvalidOperationException(ReconciliationReadBudget.CloseBlocked);
        var shift = await db.Shifts.SingleOrDefaultAsync(s => s.Id == shiftId)
            ?? throw new InvalidOperationException("Turno inexistente.");
        if (shift.RegisterId != actor.RegisterId || shift.CashierUserId != actor.UserId)
            throw new UnauthorizedAccessException("Este turno pertenece a otro usuario o caja.");
        actor = actor with { ShiftId = shift.Id };
        if (!await budget.FitsShiftAsync(db, shift) ||
            !await budget.ReserveAsync<CommandReceiptEntity>(db, "OperationId = {0}", operation) ||
            !await budget.ReserveAsync<CashupEntity>(db, "OperationId = {0}", operation))
            throw new InvalidOperationException(ReconciliationReadBudget.CloseBlocked);
        var recorded = await CashCountEvidence.BindAsync(db, actor, operation, "SHIFT_CLOSE", shiftId, cashup);
        if (shift.IsOpen == 0)
        {
            if (shift.CloseOperationId == operation && shift.ActualBalance == cashup.ClosedCashAmount && recorded is not null)
            {
                CashCountEvidence.CopyRecordedResult(recorded, cashup);
                return Map(shift);
            }
            throw new InvalidOperationException("El turno ya fue cerrado.");
        }
        if (await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, shift.Id))
            throw new InvalidOperationException("Hay cobros pendientes de conciliación en este turno.");
        if (await IncomingTransferService.HasPendingAsync(db, shiftId))
            throw new InvalidOperationException("Hay transferencias de abonos o recargas pendientes. Aplíquelas o resuelva sus fondos antes de cerrar.");
        if (await AccountCreditSettlementService.HasPendingAsync(db, shiftId))
            throw new InvalidOperationException("Hay reembolsos de saldo a favor sin resolución. Confirme si se movieron los fondos antes de cerrar.");
        var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shiftId).ToListAsync();
        decimal expected = ShiftLedgerValidator.ExpectedCash(shift, movements);
        var payments = await ShiftLedgerValidator.Payments(db, shiftId).ToListAsync();
        var sales = await ShiftLedgerValidator.Sales(db, shiftId).ToListAsync();
        ShiftLedgerValidator.ValidateSales(shift, sales, payments);
        await ShiftLedgerValidator.ValidateCashAsync(db, shift, payments, movements);
        ShiftLedgerValidator.ValidateCashProjection(shift, expected);
        shift.TotalCashSales = payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount);
        shift.TotalCreditSales = payments.Where(p => p.Kind == TenderKind.Credit).Sum(p => p.Amount);
        shift.ExpectedBalance = expected;
        shift.ActualBalance = cashup.ClosedCashAmount;
        shift.Diff = cashup.ClosedCashAmount - expected;
        shift.EndTime = DateTimeOffset.UtcNow.ToString("O");
        shift.IsOpen = 0; shift.CloseOperationId = operation; shift.Version++;
        cashup.ShiftId = shift.Id; cashup.RegisterId = actor.RegisterId; cashup.CashierUserId = actor.UserId;
        cashup.Type = "Final";
        cashup.OpenCashAmount = shift.OpeningBalance; cashup.TheoreticalCash = expected; cashup.Difference = shift.Diff.Value;
        cashup.Timestamp = shift.EndTime;
        await PopulateCashupAsync(db, cashup, captureActor: true);
        db.Cashups.Add(cashup);
        // A discrepancy is evidence, not an invented cash movement that hides the shortage.
        CommercialLedger.Journal(db, actor, operation, "SHIFT_CLOSED", shift.Id, cashup.ClosedCashAmount, $"Diferencia: {shift.Diff}");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Map(shift);
    }
    public async Task RegisterMovementAsync(decimal amount, bool outflow, string reason, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (amount <= 0 || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Indique importe y motivo.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashInOut);
        await CommandIdentity.BindAsync(db, actor, operationId, "CASH_MOVEMENT", amount, outflow, reason);
        var existing = await db.TransactionJournal.SingleOrDefaultAsync(j => j.OperationId == operationId && j.EventType == "CASH_MOVEMENT");
        if (existing is not null)
        {
            if (existing.Amount != amount || existing.ShiftId != actor.ShiftId) throw new InvalidOperationException("Clave de movimiento reutilizada.");
            return;
        }
        await CommercialLedger.CashAsync(db, actor, operationId, operationId, amount, outflow,
            outflow ? MovementType.RetiroEfectivo : MovementType.IngresoExtraordinario, reason);
        CommercialLedger.Journal(db, actor, operationId, "CASH_MOVEMENT", operationId, amount, reason);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task RecordCountAsync(string shiftId, CashupEntity cashup)
    {
        if (cashup.Type == "Final") { await CloseWithCashupAsync(shiftId, cashup); return; }
        string operation = CommercialLedger.OperationId(cashup.OperationId);
        CashCountEvidence.Validate(cashup);
        if (cashup.Type is not ("Parcial" or "Partial")) throw new InvalidOperationException("Tipo de arqueo inválido.");
        cashup.Type = "Parcial";
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashClose);
        if (actor.ShiftId != shiftId) throw new UnauthorizedAccessException("Turno ajeno.");
        var recorded = await CashCountEvidence.BindAsync(db, actor, operation, "COUNT", shiftId, cashup);
        if (recorded is not null)
        {
            CashCountEvidence.CopyRecordedResult(recorded, cashup);
            return;
        }
        var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId);
        var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shiftId).ToListAsync();
        cashup.ShiftId = shiftId; cashup.RegisterId = actor.RegisterId; cashup.CashierUserId = actor.UserId;
        cashup.OpenCashAmount = shift.OpeningBalance;
        cashup.TheoreticalCash = shift.OpeningBalance + movements.Sum(m => m.IsOutflow ? -m.Amount : m.Amount);
        cashup.Difference = cashup.ClosedCashAmount - cashup.TheoreticalCash;
        cashup.Timestamp = DateTimeOffset.UtcNow.ToString("O");
        await PopulateCashupAsync(db, cashup, captureActor: true);
        db.Cashups.Add(cashup);
        CommercialLedger.Journal(db, actor, operation, "CASH_COUNTED", cashup.Id, cashup.ClosedCashAmount, cashup.Notes);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<ShiftDto>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await Operations.RequireAsync(db, PermissionCatalog.CashHistory, false);
        return (await db.Shifts.AsNoTracking().OrderByDescending(s => s.StartTime).Take(500).ToListAsync()).Select(Map).ToList();
    }
    public async Task<List<CashupEntity>> GetCashupsAsync(DateTime start, DateTime end)
    {
        var page = await GetCashupPageAsync(start, end, 0, 200);
        if (page.HasMore) throw new InvalidOperationException("El período supera 200 cortes. Utilice la consulta paginada del historial.");
        return page.Items;
    }
    private static async Task PopulateCashupAsync(AppDbContext db, CashupEntity cashup, bool captureActor = false)
    {
        await PopulateCashupsAsync(db, [cashup], BusinessTime.Zone);
        if (captureActor && Guid.TryParse(cashup.CashierUserId, out var userId))
        {
            var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new { u.FullName, u.RoleString, u.Role }).SingleAsync();
            cashup.CashierName = user.FullName;
            cashup.CashierRole = string.IsNullOrEmpty(user.RoleString) ? user.Role.ToString() : user.RoleString;
        }
    }
    public async Task<List<ShiftDto>> GetLegacyOpenAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await Operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        return (await db.Shifts.AsNoTracking().Where(s => s.IsOpen == 1 && (s.RegisterId == null || s.CashierUserId == null)).ToListAsync()).Select(Map).ToList();
    }
    public async Task ReconcileLegacyAsync(string shiftId, decimal countedCash, string reason, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (countedCash < 0 || CommercialLedger.Money(countedCash) != countedCash || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Indique el conteo físico y la referencia al acta de conciliación histórica.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile, false);
        await Operations.RequireAsync(db, PermissionCatalog.CashClose, false);
        await CommandIdentity.BindAsync(db, actor, operationId, "LEGACY_SHIFT_CLOSE", shiftId, countedCash, reason);
        var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId);
        if (shift.CloseOperationId == operationId && shift.IsOpen == 0) return;
        if (shift.IsOpen == 0 || (shift.RegisterId is not null && shift.CashierUserId is not null))
            throw new InvalidOperationException("Este flujo sólo cierra turnos históricos sin atribución verificable.");
        shift.ActualBalance = countedCash; shift.Diff = countedCash - shift.ExpectedBalance;
        shift.EndTime = DateTimeOffset.UtcNow.ToString("O"); shift.IsOpen = 0; shift.CloseOperationId = operationId; shift.Version++;
        // Preserve unknown original attribution. The journal identifies the supervisor who reconciled, not a fictitious historical cashier.
        CommercialLedger.Journal(db, actor with { ShiftId = shiftId }, operationId, "LEGACY_SHIFT_RECONCILED", shiftId, countedCash, reason);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private static ShiftDto Map(ShiftEntity e) => new(e.Id, e.StartTime, e.EndTime, e.OpeningBalance,
        e.TotalCashSales, e.TotalCreditSales, e.ExpectedBalance, e.ActualBalance, e.Diff, e.IsOpen, e.RegisterId, e.CashierUserId);
}
