using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public partial class SaleService
{
    public async Task CancelAsync(string saleId) => await CancelSaleAsync(saleId, "Cancelación administrativa");

    public Task<bool> CancelSaleAsync(string saleId, string reason) =>
        ReturnCoreAsync(saleId, null, 0m, reason, true, $"CANCEL:{saleId}", true);

    public Task<bool> ProcessPartialReturnAsync(string saleId, string productId, double returnQty, string reason,
        string refundMethod = "Efectivo", bool isProductInGoodCondition = true, string? operationId = null)
    {
        if (!double.IsFinite(returnQty) || returnQty <= 0 || Math.Round(returnQty, 3) != returnQty)
            throw new InvalidOperationException("Cantidad de devolución inválida.");
        // The original payment determines the refund medium; the UI cannot convert card/credit into cash.
        return ReturnCoreAsync(saleId, productId, (decimal)returnQty, reason, isProductInGoodCondition,
            CommercialLedger.OperationId(operationId), false);
    }

    private async Task<bool> ReturnCoreAsync(string saleId, string? productId, decimal quantity, string reason,
        bool restock, string operationId, bool cancel)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Indique el motivo de devolución.");
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, cancel ? PermissionCatalog.PosCancelSale : PermissionCatalog.PosRefund);
        await CommandIdentity.BindAsync(db, actor, operationId, "RETURN", saleId, productId, quantity, reason, restock, cancel);
        var previous = await db.Returns.AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operationId);
        if (previous is not null)
        {
            if (previous.OriginalSaleId != saleId) throw new InvalidOperationException("La operación pertenece a otra venta.");
            return !cancel;
        }
        var sale = await db.Sales.SingleOrDefaultAsync(s => s.Id == saleId);
        if (sale is null || sale.Status is SaleStatus.Canceled or SaleStatus.Refunded) return false;
        if (sale.LedgerVersion == 0)
            throw new InvalidOperationException("Venta histórica sin desglose verificable. Requiere conciliación y migración asistida antes de devolver.");
        var lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == saleId).OrderBy(l => l.LineNumber).ToListAsync();
        var lineIds = lines.Select(l => l.Id).ToArray();
        var priorLines = await db.ReturnLines.AsNoTracking().Where(r => lineIds.Contains(r.SaleLineId)).ToListAsync();
        var targetLines = cancel ? lines : lines.Where(l => l.ProductId == productId || l.Id == productId).ToList();
        decimal available = targetLines.Sum(l => l.Quantity - priorLines.Where(r => r.SaleLineId == l.Id).Sum(r => r.Quantity));
        if (available <= 0 || (!cancel && quantity > available)) throw new InvalidOperationException("La cantidad supera lo disponible para devolución.");
        decimal remaining = cancel ? available : quantity;
        var returned = new ReturnEntity
        {
            Id = await GenerateNextReturnFolioAsync(db),
            OperationId = operationId,
            OriginalSaleId = saleId,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId,
            RefundMethod = "Medios originales",
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        db.Returns.Add(returned);
        foreach (var line in targetLines)
        {
            var oldLines = priorLines.Where(r => r.SaleLineId == line.Id).ToList();
            decimal alreadyReturned = oldLines.Sum(r => r.Quantity);
            decimal take = Math.Min(remaining, line.Quantity - alreadyReturned);
            if (take <= 0) continue;
            decimal amount = CommercialLedger.Money(line.Total * (alreadyReturned + take) / line.Quantity) - oldLines.Sum(r => r.Amount);
            decimal restoredBefore = oldLines.Where(r => r.Restocked).Sum(r => r.Quantity);
            decimal cost = restock ? CommercialLedger.Money(line.CostTotal * (restoredBefore + take) / line.Quantity)
                - oldLines.Sum(r => r.CostReversed) : 0m;
            if (restock)
            {
                // Restore the exact component recipe sold, even if the kit has since been edited.
                var originalMoves = await db.InventoryMovements.AsNoTracking()
                    .Where(m => m.SaleLineId == line.Id && m.Reason == "SALE").ToListAsync();
                foreach (var move in originalMoves)
                {
                    decimal restored = Math.Round(-move.Quantity * (restoredBefore + take) / line.Quantity, 3)
                        - Math.Round(-move.Quantity * restoredBefore / line.Quantity, 3);
                    if (restored > 0) await CommercialLedger.StockAsync(db, actor, operationId, returned.Id,
                        move.ProductId, restored, "RETURN", line.Id, move.UnitCost);
                }
            }
            db.ReturnLines.Add(new ReturnLineEntity
            {
                ReturnId = returned.Id,
                SaleLineId = line.Id,
                ProductId = line.ProductId,
                Quantity = take,
                Amount = amount,
                CostReversed = cost,
                Restocked = restock
            });
            returned.TotalRefunded += amount;
            returned.CogsReversed += cost;
            remaining -= take;
            if (remaining == 0) break;
        }
        var payments = await db.SalePayments.AsNoTracking().Where(p => p.SaleId == saleId).OrderBy(p => p.Kind).ToListAsync();
        var paymentIds = payments.Select(p => p.Id).ToArray();
        var priorRefunds = await db.PaymentRefunds.AsNoTracking().Where(r => paymentIds.Contains(r.PaymentId)).ToListAsync();
        decimal availablePayments = payments.Sum(p => p.Amount) - priorRefunds.Sum(r => r.Amount);
        decimal weight = 0m;
        decimal allocatedCumulative = 0m;
        for (int i = 0; i < payments.Count; i++)
        {
            var payment = payments[i];
            decimal availablePayment = payment.Amount - priorRefunds.Where(r => r.PaymentId == payment.Id).Sum(r => r.Amount);
            weight += availablePayment;
            decimal target = availablePayments == 0 ? 0 : CommercialLedger.Money(returned.TotalRefunded * weight / availablePayments);
            decimal amount = target - allocatedCumulative;
            allocatedCumulative = target;
            if (amount <= 0) continue;
            var refund = new PaymentRefundEntity
            {
                ReturnId = returned.Id,
                PaymentId = payment.Id,
                OperationId = operationId,
                Kind = payment.Kind,
                Amount = amount,
                ShiftId = actor.ShiftId!,
                RegisterId = actor.RegisterId,
                CashierUserId = actor.UserId
            };
            if (payment.Kind == TenderKind.Cash)
                await CommercialLedger.CashAsync(db, actor, operationId, returned.Id, amount, true, MovementType.DevolucionCliente, reason);
            else if (payment.Kind == TenderKind.Credit)
            {
                long cents = ToStoredCents(amount);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET debt = debt - {cents} WHERE id = {payment.AccountId}");
                CommercialLedger.Account(db, actor, operationId, AccountKind.Customer, payment.AccountId!, saleId, -amount, "RETURN_CREDIT");
            }
            else if (payment.Kind == TenderKind.Giftcard)
            {
                long cents = ToStoredCents(amount);
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE giftcards SET balance = balance + {cents} WHERE card_number = {payment.AccountId}") != 1)
                    throw new InvalidOperationException("No se pudo restituir el monedero.");
                CommercialLedger.Account(db, actor, operationId, AccountKind.Giftcard, payment.AccountId!, saleId, amount, "RETURN_RESTORE");
            }
            else if (payment.Kind == TenderKind.Loyalty)
            {
                double points = (double)amount;
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET puntos_saldo = puntos_saldo + {points} WHERE id = {payment.AccountId}");
                CommercialLedger.Account(db, actor, operationId, AccountKind.Loyalty, payment.AccountId!, saleId, amount, "RETURN_RESTORE");
            }
            else
            {
                // Durable obligation. Never claim an external refund occurred without provider evidence.
                refund.State = TenderState.RequiresReconciliation;
            }
            db.PaymentRefunds.Add(refund);
        }
        decimal priorQuantity = priorLines.Sum(r => r.Quantity);
        decimal newQuantity = db.ReturnLines.Local.Where(r => r.ReturnId == returned.Id).Sum(r => r.Quantity);
        bool all = priorQuantity + newQuantity == lines.Sum(l => l.Quantity);
        sale.Status = cancel ? SaleStatus.Canceled : all ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded;
        if (cancel)
        {
            sale.IsCancelled = 1; sale.CancelledAt = returned.CreatedAt; sale.CancellationReason = reason; sale.CancellationDate = returned.CreatedAt;
        }
        var earned = await db.AccountEntries.AsNoTracking().Where(e => e.DocumentId == saleId && e.EntryType == "SALE_REWARD").ToListAsync();
        foreach (var reward in earned)
        {
            decimal alreadyReversed = -(await db.AccountEntries.AsNoTracking()
                .Where(e => e.DocumentId == saleId && e.AccountKind == AccountKind.Loyalty && e.EntryType == "RETURN_REWARD")
                .ToListAsync()).Sum(e => e.Amount);
            var snapshots = System.Text.Json.JsonSerializer.Deserialize(sale.ItemsJson,
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto) ?? [];
            var allReturned = priorLines.Concat(db.ReturnLines.Local.Where(r => r.ReturnId == returned.Id)).ToList();
            decimal totalReverse = all ? reward.Amount : Math.Min(reward.Amount, CommercialLedger.Money(lines.Sum(l =>
                (decimal)(snapshots.SingleOrDefault(s => s.SaleLineId == l.Id)?.PointsRewarded ?? 0)
                * allReturned.Where(r => r.SaleLineId == l.Id).Sum(r => r.Quantity))));
            decimal reversal = totalReverse - alreadyReversed;
            if (reversal > 0)
            {
                double points = (double)reversal;
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET puntos_saldo = puntos_saldo - {points} WHERE id = {reward.AccountId}");
                CommercialLedger.Account(db, actor, operationId, AccountKind.Loyalty, reward.AccountId, saleId, -reversal, "RETURN_REWARD");
            }
        }
        CommercialLedger.Journal(db, actor, operationId, cancel ? "SALE_CANCELED" : "RETURN_COMPLETED", returned.Id, returned.TotalRefunded, reason);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    public async Task UpdateFiscalStatusAsync(string saleId, string status, string? uuid, string? folio)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await AuthorizeAsync(db, PermissionCatalog.ReportsView);
        var sale = await db.Sales.FindAsync(saleId);
        if (sale is null) return;
        sale.EstadoFiscal = status; sale.UuidSat = uuid; sale.SerieFolio = folio;
        await db.SaveChangesAsync();
    }
}
