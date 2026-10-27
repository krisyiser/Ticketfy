using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Bounded recipe snapshot shared by catalog, recipe validation and checkout.
/// Callers own a read snapshot or write transaction for the entire operation.
/// A recipe ID never substitutes for its explicitly linked parent product.
/// </summary>
internal sealed class KitInventoryGraph
{
    internal const int MaximumDirectComponents = 200;
    internal const int MaximumDepth = 32;
    internal const int MaximumProducts = 4096;
    internal const int MaximumEdges = 8192;
    private const int MaximumExpansionSteps = 10000;
    internal const decimal MaximumQuantity = long.MaxValue / 1000m;
    private const string WorkloadError = "La composición supera el límite de lectura segura. Acote el catálogo o simplifique el combo.";
    private readonly Dictionary<string, ProductEntity> _products;
    private readonly Dictionary<string, List<ItemKitItemEntity>> _edges = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly Dictionary<string, Composition> _memo = [];

    private sealed record Composition(IReadOnlyDictionary<string, decimal> Ingredients, int Depth);
    internal sealed record Plan(IReadOnlyDictionary<string, decimal> Ingredients, decimal UnitCost);

    private KitInventoryGraph(List<ProductEntity> products) => _products = products.ToDictionary(p => p.Id);

    internal static async Task<KitInventoryGraph> LoadAsync(AppDbContext db, List<ProductEntity> roots)
    {
        if (roots.Count > MaximumProducts) throw new InvalidOperationException(WorkloadError);
        var graph = new KitInventoryGraph(roots);
        var frontier = roots.Where(p => p.IsKit).Select(p => p.Id).ToHashSet();
        var expanded = new HashSet<string>();
        int edgeCount = 0;
        for (int depth = 0; frontier.Count > 0 && depth < MaximumDepth; depth++)
        {
            var ids = frontier.Where(expanded.Add).ToArray();
            if (ids.Length == 0) break;
            // Count before materializing components, including malformed legacy recipes.
            var headers = await db.ItemKits.AsNoTracking().Where(k => ids.Contains(k.ParentProductId))
                .OrderBy(k => k.Id).Select(k => new
                {
                    k.Id,
                    k.ParentProductId,
                    Count = db.ItemKitItems.LongCount(i => i.ItemKitId == k.Id)
                }).Take(MaximumProducts + 1).ToListAsync();
            if (headers.Count > MaximumProducts) { graph.Invalidate(ids); break; }
            var grouped = headers.ToLookup(k => k.ParentProductId);
            var valid = new Dictionary<string, string>();
            foreach (string id in ids)
            {
                var matches = grouped[id].ToList();
                if (matches.Count != 1)
                    graph._errors[id] = "El producto requiere una única receta vinculada; falta o está duplicada.";
                else if (matches[0].Count is < 1 or > MaximumDirectComponents)
                    graph._errors[id] = $"La receta requiere entre 1 y {MaximumDirectComponents} componentes.";
                else valid[matches[0].Id] = id;
            }
            var recipeIds = valid.Keys.ToArray();
            var parts = await db.ItemKitItems.AsNoTracking().Where(p => recipeIds.Contains(p.ItemKitId))
                .OrderBy(p => p.ItemKitId).ThenBy(p => p.Id).Take(MaximumEdges - edgeCount + 1).ToListAsync();
            if (parts.Count > MaximumEdges - edgeCount) { graph.Invalidate(valid.Values); break; }
            var missing = parts.Select(p => p.ProductId).Distinct().Where(id => !graph._products.ContainsKey(id)).ToArray();
            if (missing.Length > MaximumProducts - graph._products.Count) { graph.Invalidate(valid.Values); break; }
            var products = await db.Products.AsNoTracking().Where(p => missing.Contains(p.Id))
                .Take(MaximumProducts - graph._products.Count + 1).ToListAsync();
            foreach (var product in products) graph._products[product.Id] = product;
            var byRecipe = parts.ToLookup(p => p.ItemKitId);
            foreach (var pair in valid) graph._edges[pair.Value] = byRecipe[pair.Key].ToList();
            edgeCount += parts.Count;
            frontier = parts.Where(p => graph._products.TryGetValue(p.ProductId, out var product) && product.IsKit)
                .Select(p => p.ProductId).ToHashSet();
        }
        return graph;
    }

    internal string? CheckoutFingerprint(IEnumerable<string> productIds)
    {
        var roots = productIds.Distinct().Where(id => _products.TryGetValue(id, out var product) && product.IsKit)
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (roots.Length == 0) return null;
        // Stock and current costs are intentionally excluded: this protects the physical recipe, not a reservation.
        return CommandIdentity.Hash("KIT-COMPOSITION-V1", string.Concat(roots.Select(id => CommandIdentity.Hash(id,
            string.Concat(RequirePlan(id).Ingredients.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => CommandIdentity.Hash(p.Key, p.Value)))))));
    }

    private void Invalidate(IEnumerable<string> ids)
    {
        foreach (string id in ids) _errors[id] = WorkloadError;
    }

    internal Plan RequirePlan(string productId)
    {
        try
        {
            int steps = 0;
            var composition = Expand(productId, [], ref steps);
            decimal cost = 0m;
            foreach (var item in composition.Ingredients)
            {
                var product = _products[item.Key];
                if (product.Cost < 0 || product.Cost > long.MaxValue / 100m || decimal.Round(product.Cost, 2) != product.Cost)
                    throw new InvalidOperationException("El costo de un ingrediente requiere revisión.");
                cost = checked(cost + item.Value * product.Cost);
            }
            if (cost > long.MaxValue / 100m) throw new InvalidOperationException("El costo del combo supera el importe admitido.");
            return new(composition.Ingredients, cost);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("La composición excede la precisión o cantidad admitida.");
        }
    }

    internal (double Stock, string? Warning) Availability(string productId)
    {
        try
        {
            var plan = RequirePlan(productId);
            decimal available = plan.Ingredients.Min(i => decimal.Floor(
                Quantity(_products[i.Key].Stock, _products[i.Key].IsBulk, allowZero: true) / i.Value));
            return ((double)Math.Max(0, available), null);
        }
        catch (InvalidOperationException ex) { return (0, "Combo no disponible: " + ex.Message); }
    }

    internal IReadOnlyDictionary<string, decimal> QuantitiesFor(Plan plan, decimal quantity)
    {
        var result = new Dictionary<string, decimal>();
        try
        {
            foreach (var pair in plan.Ingredients)
            {
                decimal required = checked(pair.Value * quantity);
                ValidateAmount(required, _products[pair.Key].IsBulk);
                result.Add(pair.Key, required);
            }
        }
        catch (OverflowException) { throw new InvalidOperationException("La cantidad solicitada excede el límite admitido."); }
        return result;
    }

    internal void RequireAvailable(IReadOnlyDictionary<string, decimal> requirements)
    {
        foreach (var pair in requirements)
        {
            ValidateAmount(pair.Value, _products[pair.Key].IsBulk);
            if (Quantity(_products[pair.Key].Stock, _products[pair.Key].IsBulk, allowZero: true) < pair.Value)
                throw new InvalidOperationException("Stock insuficiente para las partidas y sus ingredientes compartidos. Actualice el carrito.");
        }
    }

    private Composition Expand(string id, HashSet<string> path, ref int steps)
    {
        if (++steps > MaximumExpansionSteps) throw new InvalidOperationException(WorkloadError);
        if (!_products.TryGetValue(id, out var product) || !product.IsActive)
            throw new InvalidOperationException("La receta contiene un producto inexistente o inactivo.");
        if (_errors.TryGetValue(id, out var error)) throw new InvalidOperationException(error);
        if (_memo.TryGetValue(id, out var cached))
        {
            if (path.Count + cached.Depth > MaximumDepth) throw new InvalidOperationException("El combo supera 32 niveles.");
            return cached;
        }
        if (!product.IsKit)
        {
            Quantity(product.Stock, product.IsBulk, allowZero: true);
            return _memo[id] = new(new Dictionary<string, decimal> { [id] = 1m }, 0);
        }
        if (path.Count >= MaximumDepth) throw new InvalidOperationException("El combo supera 32 niveles.");
        if (!path.Add(id)) throw new InvalidOperationException("La receta contiene un ciclo de componentes.");
        try
        {
            if (!_edges.TryGetValue(id, out var children) || children.Count == 0)
                throw new InvalidOperationException("La composición está incompleta o supera 32 niveles.");
            var required = new Dictionary<string, decimal>();
            int depth = 0;
            foreach (var child in children)
            {
                if (!_products.TryGetValue(child.ProductId, out var ingredient))
                    throw new InvalidOperationException("La receta contiene un producto inexistente.");
                decimal amount = Quantity(child.Quantity, ingredient.IsBulk);
                var expanded = Expand(child.ProductId, path, ref steps);
                depth = Math.Max(depth, expanded.Depth);
                foreach (var pair in expanded.Ingredients)
                    required[pair.Key] = checked(required.GetValueOrDefault(pair.Key) + amount * pair.Value);
            }
            foreach (var pair in required) ValidateAmount(pair.Value, _products[pair.Key].IsBulk);
            return _memo[id] = new(required, depth + 1);
        }
        finally { path.Remove(id); }
    }

    private static decimal Quantity(double quantity, bool fractional, bool allowZero = false)
    {
        if (!double.IsFinite(quantity) || quantity > (double)MaximumQuantity || Math.Round(quantity, 3) != quantity)
            throw new InvalidOperationException("Una cantidad de la composición es inválida o supera tres decimales.");
        decimal amount = (decimal)quantity;
        ValidateAmount(amount, fractional, allowZero);
        return amount;
    }

    private static void ValidateAmount(decimal amount, bool fractional, bool allowZero = false)
    {
        if (amount < 0 || (!allowZero && amount == 0) || amount > MaximumQuantity || decimal.Round(amount, 3) != amount
            || (!fractional && decimal.Truncate(amount) != amount))
            throw new InvalidOperationException("Una cantidad de la composición no puede representarse en la unidad del ingrediente.");
    }
}
