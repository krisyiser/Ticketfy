using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CatalogKitAvailability
{
    public static async Task<Dictionary<string, (double Stock, string? Warning)>> CalculateAsync(AppDbContext db, List<ProductEntity> roots)
    {
        var graph = await KitInventoryGraph.LoadAsync(db, roots);
        return roots.Where(p => p.IsKit).ToDictionary(p => p.Id, p => graph.Availability(p.Id));
    }
}
