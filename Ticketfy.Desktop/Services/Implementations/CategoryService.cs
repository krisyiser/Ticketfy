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
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        var names = await db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => p.Category).ToListAsync();
        return categories.Select(c => new CategorySummary(c.Id, c.Name, names.Count(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
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
            var products = await db.Products.Where(p => p.Category.ToLower() == category.Name.ToLower()).ToListAsync();
            var promotions = await db.Promotions.Where(p => p.TargetCategory != null && p.TargetCategory.ToLower() == category.Name.ToLower()).ToListAsync();
            if (promotions.Count > 0) await operations.RequireAsync(db, PermissionCatalog.PromotionsManage, false);
            foreach (var p in products) { p.Category = name; p.StockVersion++; }
            foreach (var p in promotions) { if (delete) p.IsActive = 0; else p.TargetCategory = name; }
            if (delete) db.Categories.Remove(category); else category.Name = name;
        }
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), delete ? "CATEGORY_REMOVED" : "CATEGORY_SAVED", id, 0,
            delete ? "Productos reasignados a General; promociones vinculadas inactivadas" : "Administración de catálogo");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
