using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Implementations;

public sealed class AlertBackgroundWorker(IDbContextFactory<AppDbContext> factory,
    TimeSpan? interval = null, TimeProvider? clock = null, MaintenanceHealth? health = null)
    : PeriodicBackgroundWorker(interval ?? TimeSpan.FromMinutes(5), clock, health, MaintenanceTask.StockAlerts)
{
    internal const int BatchSize = 200;
    internal override async Task RunOnceAsync(CancellationToken token)
    {
        string? after = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
            // The writer transaction serializes this decision with checkout's own low-stock alert.
            await using var tx = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var query = db.Products.AsNoTracking().Where(p => p.IsActive && !p.IsKit && p.Stock <= p.MinStock
                && !db.SystemAlerts.Any(a => a.ProductId == p.Id && !a.IsResolved));
            if (after is not null) query = query.Where(p => string.Compare(p.Id, after) > 0);
            var rows = await query.OrderBy(p => p.Id).Select(p => new { p.Id, p.Name, p.Stock, p.MinStock, p.DefaultSupplierId })
                .Take(BatchSize).ToListAsync(token).ConfigureAwait(false);
            foreach (var product in rows)
            {
                db.SystemAlerts.Add(new SystemAlertEntity
                {
                    ProductId = product.Id,
                    SupplierId = product.DefaultSupplierId,
                    Title = "Stock bajo",
                    Message = $"{product.Name}: existencias {product.Stock}; mínimo {product.MinStock}.",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                });
            }
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
            if (rows.Count < BatchSize) return;
            after = rows[^1].Id;
            await Task.Yield();
        }
    }
}
