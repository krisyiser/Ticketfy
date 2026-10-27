using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class PredictiveIntelligenceService : IPredictiveIntelligenceService
{
    internal const int CandidatePageSize = 32;
    internal const int MaximumCartProducts = 1000;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly CommercialOperationContext? _operations;
    private readonly TimeProvider _clock;

    public PredictiveIntelligenceService(IDbContextFactory<AppDbContext> contextFactory,
        CommercialOperationContext? operations = null, TimeProvider? clock = null)
    { _contextFactory = contextFactory; _operations = operations; _clock = clock ?? TimeProvider.System; }

    public async Task<ProductDto?> GetTopCorrelatedProductAsync(string sourceProductId, List<string> currentCartProductIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProductId);
        ArgumentNullException.ThrowIfNull(currentCartProductIds);
        if (currentCartProductIds.Count > MaximumCartProducts)
            throw new InvalidOperationException("La consulta de recomendaciones admite hasta 1.000 partidas. No se consultó un carrito truncado.");
        cancellationToken.ThrowIfCancellationRequested();
        var excluded = currentCartProductIds.Append(sourceProductId).Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.PosCheckout, false);

        // Legacy data can contain both directions. Rank the strongest observation, never add them twice.
        var ranked = db.CoOccurrences.AsNoTracking().Where(c => c.Frecuencia > 0 &&
                (c.ProductoA == sourceProductId || c.ProductoB == sourceProductId))
            .Select(c => new { ProductId = c.ProductoA == sourceProductId ? c.ProductoB : c.ProductoA, c.Frecuencia })
            .Where(c => !excluded.Contains(c.ProductId))
            .GroupBy(c => c.ProductId)
            .Select(g => new { ProductId = g.Key, Frequency = g.Max(c => c.Frecuencia) });
        var candidates = from rank in ranked
                         join product in db.Products.AsNoTracking() on rank.ProductId equals product.Id
                         where product.IsActive && (product.IsKit || product.Stock > 0)
                         select new { Product = product, rank.Frequency };
        int lastFrequency = int.MaxValue;
        string? lastId = null;
        var mapper = new ProductService(_contextFactory, _operations);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = candidates;
            if (lastId is not null)
                query = query.Where(c => c.Frequency < lastFrequency ||
                    c.Frequency == lastFrequency && string.Compare(c.Product.Id, lastId) > 0);
            var page = await query.OrderByDescending(c => c.Frequency).ThenBy(c => c.Product.Id)
                .Take(CandidatePageSize).ToListAsync(cancellationToken);
            var available = await mapper.MapAvailableAsync(db, page.Select(c => c.Product).ToList());
            cancellationToken.ThrowIfCancellationRequested();
            var match = available.FirstOrDefault(p => p.IsActive && double.IsFinite(p.Stock) && p.Stock > 0 &&
                p.Stock <= (double)KitInventoryGraph.MaximumQuantity && p.InventoryWarning is null);
            if (match is not null) return match;
            if (page.Count < CandidatePageSize) return null;
            lastFrequency = page[^1].Frequency; lastId = page[^1].Product.Id;
        }
    }

    public async Task<List<PredictiveAlertDto>> GetUrgentRestockAlertsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        string lower = now.AddDays(-28).ToString("O"), upper = now.ToString("O");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.InventoryView, false);
        var consumption = db.InventoryMovements.AsNoTracking()
            .Where(m => m.SaleLineId != null && (m.Reason == "SALE" || m.Reason == "RETURN") &&
                string.Compare(m.CreatedAtUtc, lower) >= 0 && string.Compare(m.CreatedAtUtc, upper) <= 0)
            .GroupBy(m => m.ProductId).Select(g => new { ProductId = g.Key, Quantity = -g.Sum(m => m.Quantity) });
        var rows = from demand in consumption
                   join product in db.Products.AsNoTracking() on demand.ProductId equals product.Id
                   where product.IsActive && !product.IsKit && demand.Quantity > 0
                   select new { product.Id, product.Name, product.Stock, demand.Quantity };
        // The database aggregates; the client streams scalar rows and retains at most twenty candidates.
        var best = new List<(string Id, string Name, decimal Days)>(21);
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(row.Stock) || row.Stock < 0 || row.Stock > (double)KitInventoryGraph.MaximumQuantity)
                throw new InvalidDataException("Una existencia no válida impide estimar el resurtido. Revise el inventario.");
            decimal days = (decimal)row.Stock / (row.Quantity / 28m);
            if (days > 3m) continue;
            best.Add((row.Id, row.Name, days));
            best.Sort((left, right) =>
            {
                int order = left.Days.CompareTo(right.Days);
                return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
            });
            if (best.Count > 20) best.RemoveAt(20);
        }
        return best.Select(row => new PredictiveAlertDto
        {
            ProductId = row.Id,
            DaysRemaining = row.Days,
            Message = $"Revise {row.Name}: cobertura estimada {Math.Round(row.Days, 1)} días según consumo físico neto de 28 días. Es una estimación, no una orden de compra."
        }).ToList();
    }
}
