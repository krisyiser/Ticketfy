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
        SaleReturnEvidence.Key(saleId); SaleReturnEvidence.Key(operationId);
        if (!cancel) SaleReturnEvidence.Key(productId);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || reason.Contains('\0'))
            throw new InvalidOperationException("Indique un motivo de devolución de hasta 500 caracteres, sin nulos.");
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, cancel ? PermissionCatalog.PosCancelSale : PermissionCatalog.PosRefund);
        await CommandIdentity.BindAsync(db, actor, operationId, "RETURN", saleId, productId, quantity, reason, restock, cancel);
        if (await db.Returns.AnyAsync(r => r.OperationId == operationId))
        {
            if (await db.Returns.AnyAsync(r => r.OperationId == operationId && r.OriginalSaleId != saleId))
                throw new InvalidOperationException("La operación pertenece a otra venta.");
            return !cancel;
        }
        var evidence = await SaleReturnEvidence.ReadAsync(db, saleId);
        if (evidence is null) return false;
        var sale = evidence.Sale;
        var lines = evidence.Lines;
        var targetLines = cancel ? lines : lines.Where(l => l.ProductId == productId || l.Id == productId).ToList();
        decimal available = targetLines.Sum(l => l.Quantity - (evidence.Returned.GetValueOrDefault(l.Id)?.Quantity ?? 0m));
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
            var old = evidence.Returned.GetValueOrDefault(line.Id) ?? new SaleReturnEvidence.LineTotals();
            decimal alreadyReturned = old.Quantity;
            decimal take = Math.Min(remaining, line.Quantity - alreadyReturned);
            if (take <= 0) continue;
            decimal amount = CommercialLedger.Money(line.Total * (alreadyReturned + take) / line.Quantity) - old.Amount;
            decimal restoredBefore = old.Restocked;
            decimal cost = restock ? CommercialLedger.Money(line.CostTotal * (restoredBefore + take) / line.Quantity)
                - old.Cost : 0m;
            if (restock)
            {
                // Restore the exact component recipe sold, even if the kit has since been edited.
                foreach (var move in evidence.Stock[line.Id])
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
        var payments = evidence.Payments;
        decimal availablePayments = payments.Sum(p => p.Amount) - evidence.Refunded.Values.Sum();
        if (returned.TotalRefunded < 0 || returned.TotalRefunded > availablePayments) SaleReturnEvidence.Fail();
        decimal weight = 0m;
        decimal allocatedCumulative = 0m;
        for (int i = 0; i < payments.Count; i++)
        {
            var payment = payments[i];
            decimal availablePayment = payment.Amount - evidence.Refunded.GetValueOrDefault(payment.Id);
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
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET debt = debt - {cents} WHERE id = {payment.AccountId}") != 1)
                    SaleReturnEvidence.Fail();
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
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET puntos_saldo = puntos_saldo + {points} WHERE id = {payment.AccountId}") != 1)
                    SaleReturnEvidence.Fail();
                CommercialLedger.Account(db, actor, operationId, AccountKind.Loyalty, payment.AccountId!, saleId, amount, "RETURN_RESTORE");
            }
            else
            {
                // Durable obligation. Never claim an external refund occurred without provider evidence.
                refund.State = TenderState.RequiresReconciliation;
            }
            db.PaymentRefunds.Add(refund);
        }
        if (allocatedCumulative != returned.TotalRefunded) SaleReturnEvidence.Fail();
        decimal priorQuantity = evidence.Returned.Values.Sum(r => r.Quantity);
        decimal newQuantity = db.ReturnLines.Local.Where(r => r.ReturnId == returned.Id).Sum(r => r.Quantity);
        bool all = priorQuantity + newQuantity == lines.Sum(l => l.Quantity);
        sale.Status = cancel ? SaleStatus.Canceled : all ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded;
        if (cancel)
        {
            sale.IsCancelled = 1; sale.CancelledAt = returned.CreatedAt; sale.CancellationReason = reason; sale.CancellationDate = returned.CreatedAt;
        }
        if (evidence.RewardEarned > 0)
        {
            var newLines = db.ReturnLines.Local.Where(r => r.ReturnId == returned.Id).ToDictionary(r => r.SaleLineId);
            decimal totalReverse = all ? evidence.RewardEarned : Math.Min(evidence.RewardEarned, CommercialLedger.Money(evidence.Items.Sum(item =>
                (decimal)item.PointsRewarded * ((evidence.Returned.GetValueOrDefault(item.SaleLineId!)?.Quantity ?? 0m) +
                    (newLines.GetValueOrDefault(item.SaleLineId!)?.Quantity ?? 0m)))));
            decimal reversal = totalReverse - evidence.RewardReversed;
            if (reversal > 0)
            {
                double points = (double)reversal;
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET puntos_saldo = puntos_saldo - {points} WHERE id = {sale.CustomerId}") != 1)
                    SaleReturnEvidence.Fail();
                CommercialLedger.Account(db, actor, operationId, AccountKind.Loyalty, sale.CustomerId!, saleId, -reversal, "RETURN_REWARD");
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
