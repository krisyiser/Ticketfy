using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public partial class ItemKitService
{
    public async Task<KitCapabilities> GetCapabilitiesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var operations = _operations ?? throw new UnauthorizedAccessException("Sesión requerida.");
        var actor = await operations.RequireAsync(db, PermissionCatalog.InventoryView, false);
        async Task<bool> Allowed(string permission)
        {
            try { return await operations.RequireAsync(db, permission, false) == actor; }
            catch (UnauthorizedAccessException) { return false; }
        }
        var result = new KitCapabilities(await Allowed(PermissionCatalog.InventoryCreate),
            await Allowed(PermissionCatalog.InventoryEdit), await Allowed(PermissionCatalog.InventoryDelete));
        if (await operations.RequireAsync(db, PermissionCatalog.InventoryView, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió; actualice el directorio.");
        return result;
    }

    public async Task<KitDraft?> GetForEditAsync(string productId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, PermissionCatalog.InventoryEdit, false);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId && p.IsKit);
        if (product is null) return null;
        var recipes = await db.ItemKits.AsNoTracking().Where(k => k.ParentProductId == productId).Take(2).ToListAsync();
        if (recipes.Count != 1) throw new KitCommandRejectedException("El vínculo de la receta requiere revisión antes de editar.");
        var recipe = recipes[0];
        var items = await ComponentsAsync(db, db.ItemKitItems.Where(i => i.ItemKitId == recipe.Id), 0,
            KitInventoryGraph.MaximumDirectComponents + 1);
        if (items.Count > KitInventoryGraph.MaximumDirectComponents)
            throw new KitCommandRejectedException("La receta supera 200 componentes; no se abrirá un borrador truncado.");
        return new(product.Id, product.Barcode ?? "", product.Name, product.Price, recipe.Description,
            items, product.StockVersion, product.IsActive);
    }

    // Product is the POS master for name, barcode, price and activation. Do not display a stale kit copy.
    private sealed class HeaderRead
    {
        public string Id { get; init; } = "";
        public string ProductId { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string Name { get; init; } = "";
        public decimal Price { get; init; }
        public bool IsActive { get; init; }
        public long ComponentCount { get; init; }
        public bool HasCatalogMismatch { get; init; }
        public KitDirectoryRow ToRow() => new(Id, ProductId, Barcode, Name, Price, IsActive, ComponentCount, HasCatalogMismatch);
    }
    private static IQueryable<HeaderRead> Headers(AppDbContext db) =>
        from kit in db.ItemKits.AsNoTracking()
        join product in db.Products.AsNoTracking() on kit.ParentProductId equals product.Id into parents
        from product in parents.DefaultIfEmpty()
        select new HeaderRead
        {
            Id = kit.Id,
            ProductId = kit.ParentProductId,
            Barcode = product == null ? kit.KitBarcode : product.Barcode ?? "",
            Name = product == null ? kit.Name : product.Name,
            Price = product == null ? kit.Price : product.Price,
            IsActive = product != null && product.IsActive && product.IsKit,
            ComponentCount = db.ItemKitItems.LongCount(i => i.ItemKitId == kit.Id),
            HasCatalogMismatch = product == null || !product.IsKit || product.Barcode != kit.KitBarcode || product.Name != kit.Name || product.Price != kit.Price
        };

    private static Task<List<ItemKitItemDto>> ComponentsAsync(AppDbContext db, IQueryable<ItemKitItemEntity> query, int offset, int limit)
    {
        var bounded = query.AsNoTracking().OrderBy(i => i.Id).Skip(offset).Take(limit);
        return (from item in bounded
                join product in db.Products.AsNoTracking() on item.ProductId equals product.Id into products
                from product in products.DefaultIfEmpty()
                orderby item.Id
                select new ItemKitItemDto(item.Id, item.ItemKitId, item.ProductId,
                    product == null ? "Producto no encontrado" : product.Name, item.Quantity)).ToListAsync();
    }

    private Task AuthorizeReadAsync(AppDbContext db) =>
        (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.InventoryView, false);

    public async Task<CatalogPage<KitDirectoryRow>> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var query = Headers(db).Where(k => includeInactive || k.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string value = search.Trim().ToLowerInvariant();
            query = query.Where(k => k.Name.ToLower().Contains(value) || k.Barcode.ToLower().Contains(value)
                || k.Id.ToLower().Contains(value) || k.ProductId.ToLower().Contains(value));
        }
        var rows = await query.OrderBy(k => k.Name).ThenBy(k => k.Id).Skip(offset).Take(size + 1).ToListAsync();
        return new(rows.Take(size).Select(r => r.ToRow()).ToList(), rows.Count > size);
    }

    public async Task<KitComponentPage?> GetComponentsPageAsync(string id, int page, int size = 100)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var header = await Headers(db).SingleOrDefaultAsync(k => k.Id == id);
        if (header is null) return null;
        string description = await db.ItemKits.Where(k => k.Id == id).Select(k => k.Description).SingleAsync();
        var rows = await ComponentsAsync(db, db.ItemKitItems.Where(i => i.ItemKitId == id), offset, size + 1);
        return new(header.ToRow(), description, rows.Take(size).ToList(), rows.Count > size);
    }

    public async Task<List<ItemKitDto>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var headers = await Headers(db).Where(k => k.IsActive).OrderBy(k => k.Name).ThenBy(k => k.Id)
            .Take(LegacyCatalogReadPolicy.Limit + 1).ToListAsync();
        LegacyCatalogReadPolicy.RequireComplete(headers.Count > LegacyCatalogReadPolicy.Limit);
        // Legacy callers request whole recipes. Never return a silently truncated bill of materials.
        RequireBoundedRecipes(headers);
        var ids = headers.Select(k => k.Id).ToArray();
        var descriptions = await db.ItemKits.Where(k => ids.Contains(k.Id)).Select(k => new { k.Id, k.Description }).ToDictionaryAsync(k => k.Id);
        var items = (await ComponentsAsync(db, db.ItemKitItems.Where(i => ids.Contains(i.ItemKitId)), 0,
            LegacyCatalogReadPolicy.Limit * LegacyCatalogReadPolicy.Limit + 1)).ToLookup(i => i.ItemKitId);
        return headers.Select(k => new ItemKitDto(k.Id, k.Barcode, k.Name, k.Price, descriptions[k.Id].Description, items[k.Id].ToList())).ToList();
    }

    public async Task<ItemKitDto?> GetByBarcodeAsync(string barcode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        var matches = await Headers(db).Where(k => k.IsActive && k.Barcode == barcode.Trim()).Take(2).ToListAsync();
        if (matches.Count > 1) throw new InvalidOperationException("Más de una receta comparte el producto; revise el vínculo del combo.");
        var header = matches.SingleOrDefault();
        if (header is null) return null;
        RequireBoundedRecipes([header]);
        var items = await ComponentsAsync(db, db.ItemKitItems.Where(i => i.ItemKitId == header.Id), 0, LegacyCatalogReadPolicy.Limit + 1);
        string description = await db.ItemKits.Where(k => k.Id == header.Id).Select(k => k.Description).SingleAsync();
        return new(header.Id, header.Barcode, header.Name, header.Price, description, items);
    }

    private static void RequireBoundedRecipes(List<HeaderRead> headers)
    {
        if (headers.Any(k => k.ComponentCount > LegacyCatalogReadPolicy.Limit))
            throw new InvalidOperationException("Un combo supera 200 componentes. Consulte su composición paginada; no se devolvió una receta incompleta.");
    }
}
