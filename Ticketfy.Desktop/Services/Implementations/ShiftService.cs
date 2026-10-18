using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class ShiftService : IShiftService
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
        if (_operations is null) return null;
        var entity = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.IsOpen == 1 && s.RegisterId == _operations.RegisterId);
        return entity is null ? null : Map(entity);
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
        if (cashup.ClosedCashAmount < 0 || CommercialLedger.Money(cashup.ClosedCashAmount) != cashup.ClosedCashAmount)
            throw new InvalidOperationException("Conteo de caja inválido.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashClose, false);
        var shift = await db.Shifts.SingleOrDefaultAsync(s => s.Id == shiftId)
            ?? throw new InvalidOperationException("Turno inexistente.");
        if (shift.RegisterId != actor.RegisterId || shift.CashierUserId != actor.UserId)
            throw new UnauthorizedAccessException("Este turno pertenece a otro usuario o caja.");
        actor = actor with { ShiftId = shift.Id };
        await CommandIdentity.BindAsync(db, actor, operation, "SHIFT_CLOSE", shiftId, cashup.ClosedCashAmount, cashup.Notes);
        if (shift.IsOpen == 0)
        {
            if (shift.CloseOperationId == operation && shift.ActualBalance == cashup.ClosedCashAmount) return Map(shift);
            throw new InvalidOperationException("El turno ya fue cerrado.");
        }
        if (await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, shift.Id))
            throw new InvalidOperationException("Hay cobros pendientes de conciliación en este turno.");
        if (await IncomingTransferService.HasPendingAsync(db, shiftId))
            throw new InvalidOperationException("Hay transferencias de abonos o recargas pendientes. Aplíquelas o resuelva sus fondos antes de cerrar.");
        var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shiftId).ToListAsync();
        decimal expected = ShiftLedgerValidator.ExpectedCash(shift, movements);
        var payments = await db.SalePayments.AsNoTracking().Where(p => p.ShiftId == shiftId).ToListAsync();
        var sales = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shiftId).ToListAsync();
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
        if (cashup.ClosedCashAmount < 0 || CommercialLedger.Money(cashup.ClosedCashAmount) != cashup.ClosedCashAmount) throw new InvalidOperationException("Conteo inválido.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await Operations.RequireAsync(db, PermissionCatalog.CashClose);
        if (actor.ShiftId != shiftId) throw new UnauthorizedAccessException("Turno ajeno.");
        await CommandIdentity.BindAsync(db, actor, operation, "COUNT", shiftId, cashup.ClosedCashAmount, cashup.Notes);
        if (await db.Cashups.AnyAsync(c => c.OperationId == operation)) return;
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
        await using var db = await _factory.CreateDbContextAsync();
        await Operations.RequireAsync(db, PermissionCatalog.CashHistory, false);
        var cashups = (await db.Cashups.AsNoTracking().ToListAsync()).Where(c => c.Timestamp.IsInDateRange(start, end)).ToList();
        foreach (var cashup in cashups) await PopulateCashupAsync(db, cashup);
        return cashups.OrderByDescending(c => c.Timestamp).ToList();
    }
    private static async Task PopulateCashupAsync(AppDbContext db, CashupEntity cashup, bool captureActor = false)
    {
        if (cashup.RegisterId is null || cashup.ShiftId is null || !DateTimeOffset.TryParse(cashup.Timestamp, out var end)) return;
        var movements = (await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == cashup.ShiftId).ToListAsync())
            .Where(m => DateTimeOffset.TryParse(m.Timestamp, out var at) && at <= end).ToList();
        cashup.TotalSales = movements.Where(m => m.MovementType == MovementType.VentaEfectivo && !m.IsOutflow).Sum(m => m.Amount);
        cashup.TotalExpenses = movements.Where(m => m.IsOutflow).Sum(m => m.Amount);
        if (captureActor && Guid.TryParse(cashup.CashierUserId, out var userId))
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
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
