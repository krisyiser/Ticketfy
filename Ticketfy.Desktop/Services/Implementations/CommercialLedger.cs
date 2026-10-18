using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CommercialLedger
{
    public static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);
    public static string OperationId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128
        ? id.Trim() : throw new InvalidOperationException("Se requiere una clave idempotente de operación.");

    public static void Journal(AppDbContext db, OperationIdentity actor, string operation, string type, string document, decimal amount, string reason)
        => db.TransactionJournal.Add(new TransactionJournalEntryEntity
        {
            OperationId = operation,
            EventType = type,
            EntityId = document,
            Amount = Money(amount),
            Details = reason,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId
        });

    public static void Account(AppDbContext db, OperationIdentity actor, string operation, AccountKind kind,
        string account, string document, decimal amount, string type, string? dueDate = null)
        => db.AccountEntries.Add(new AccountEntryEntity
        {
            OperationId = operation,
            AccountKind = kind,
            AccountId = account,
            DocumentId = document,
            Amount = Money(amount),
            EntryType = type,
            DueDate = dueDate,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId
        });

    public static async Task CashAsync(AppDbContext db, OperationIdentity actor, string operation,
        string document, decimal amount, bool outflow, MovementType type, string reason)
    {
        if (amount == 0m) return;
        if (amount < 0m || Money(amount) != amount || actor.ShiftId is null)
            throw new InvalidOperationException("Movimiento de caja inválido.");
        var shift = await db.Shifts.SingleAsync(s => s.Id == actor.ShiftId && s.IsOpen == 1 && s.RegisterId == actor.RegisterId);
        shift.ExpectedBalance += outflow ? -amount : amount;
        if (type == MovementType.VentaEfectivo && !outflow) shift.TotalCashSales += amount;
        db.ShiftMovements.Add(new ShiftMovementEntity
        {
            OperationId = operation,
            ShiftId = shift.Id,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            ReferenceId = document,
            Amount = amount,
            IsOutflow = outflow,
            MovementType = type,
            Description = reason,
            Timestamp = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public static async Task<decimal> StockAsync(AppDbContext db, OperationIdentity actor, string operation,
        string document, string productId, decimal quantity, string reason, string? saleLineId = null, decimal? cost = null)
    {
        if (quantity == 0m || Math.Round(quantity, 3) != quantity)
            throw new InvalidOperationException("La cantidad debe ser distinta de cero y tener hasta tres decimales.");
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId)
            ?? throw new InvalidOperationException("Producto no encontrado.");
        var before = (decimal)product.Stock;
        var after = before + quantity;
        if (after < 0m) throw new InvalidOperationException($"Stock insuficiente para {product.Name}.");
        double stored = (double)after;
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE products SET stock = {stored}, stock_version = stock_version + 1
            WHERE id = {productId} AND stock_version = {product.StockVersion};");
        if (changed != 1) throw new DbUpdateConcurrencyException("El inventario cambió; vuelva a intentar la operación.");
        db.InventoryMovements.Add(new InventoryMovementEntity
        {
            ProductId = productId,
            OperationId = operation,
            DocumentId = document,
            SaleLineId = saleLineId,
            Quantity = quantity,
            StockBefore = before,
            StockAfter = after,
            UnitCost = cost ?? product.Cost,
            Reason = reason,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId
        });
        return product.Cost;
    }
}
