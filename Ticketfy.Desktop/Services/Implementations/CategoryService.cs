using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed record CategorySummary(string Id, string Name, int ProductCount);

public sealed class CategoryService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public async Task<List<CategorySummary>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await operations.RequireAsync(db, PermissionCatalog.InventoryView, false);
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        var groups = await db.Products.AsNoTracking().Where(p => p.IsActive).GroupBy(p => p.Category)
            .Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync();
        var counts = groups.Where(g => g.Name is not null).GroupBy(g => g.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(row => row.Count), StringComparer.OrdinalIgnoreCase);
        return categories.Select(c => new CategorySummary(c.Id, c.Name, counts.GetValueOrDefault(c.Name))).ToList();
    }
    public Task SaveAsync(string id, string name) => ChangeAsync(id, name, false);
    public Task DeleteAsync(string id) => ChangeAsync(id, "General", true);
    private async Task ChangeAsync(string id, string name, bool delete)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("El nombre de categoría es obligatorio.");
        name = name.Trim();
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.InventoryEdit, false);
        var category = await db.Categories.FindAsync(id);
        if (category is null && delete) return;
        if (category is not null && category.Name.Equals("General", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La categoría General no se renombra ni elimina.");
        if (!delete && await db.Categories.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException("La categoría ya existe.");
        if (category is null) db.Categories.Add(new CategoryEntity { Id = id, Name = name });
        else
        {
            var products = db.Products.Where(p => p.Category.ToLower() == category.Name.ToLower());
            var promotions = db.Promotions.Where(p => p.TargetCategory != null && p.TargetCategory.ToLower() == category.Name.ToLower());
            if (await promotions.AnyAsync()) await operations.RequireAsync(db, PermissionCatalog.PromotionsManage, false);
            if (await promotions.AnyAsync(p => p.Revision < 0 || p.Revision == long.MaxValue))
                throw new InvalidOperationException("Una promoción agotó su revisión; requiere revisión técnica antes de modificar la categoría.");
            await products.ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Category, name)
                .SetProperty(p => p.StockVersion, p => p.StockVersion + 1));
            if (delete) await promotions.ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IsActive, 0).SetProperty(p => p.Revision, p => p.Revision + 1));
            else await promotions.ExecuteUpdateAsync(setters => setters.SetProperty(p => p.TargetCategory, name).SetProperty(p => p.Revision, p => p.Revision + 1));
            if (delete) db.Categories.Remove(category); else category.Name = name;
        }
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), delete ? "CATEGORY_REMOVED" : "CATEGORY_SAVED", id, 0,
            delete ? "Productos reasignados a General; promociones vinculadas inactivadas" : "Administración de catálogo");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
