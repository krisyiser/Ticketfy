using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

public class ItemKitService : IItemKitService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;

    public ItemKitService(AppDbContext db, CommercialOperationContext? operations = null)
    { _factory = new AppDbContextFactoryImpl<AppDbContext>(db.Options); _operations = operations; }

    public async Task<List<ItemKitDto>> GetAllAsync()
    {
        await using var _db = await _factory.CreateDbContextAsync();
        try
        {
            var kits = await _db.ItemKits.Where(k => _db.Products.Any(p => p.Id == k.ParentProductId && p.IsActive)).ToListAsync();
            var allKitItems = await _db.ItemKitItems.ToListAsync();
            var products = await _db.Products.ToDictionaryAsync(p => p.Id, p => p.Name);

            var result = new List<ItemKitDto>();
            foreach (var k in kits)
            {
                var items = allKitItems
                    .Where(ki => ki.ItemKitId == k.Id)
                    .Select(ki => new ItemKitItemDto(
                        ki.Id,
                        ki.ItemKitId,
                        ki.ProductId,
                        products.TryGetValue(ki.ProductId, out var pName) ? pName : "Producto no encontrado",
                        ki.Quantity
                    ))
                    .ToList();

                result.Add(new ItemKitDto(k.Id, k.KitBarcode, k.Name, k.Price, k.Description, items));
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting item kits");
            return [];
        }
    }

    public async Task<ItemKitDto?> GetByBarcodeAsync(string barcode)
    {
        await using var _db = await _factory.CreateDbContextAsync();
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        try
        {
            var kit = await _db.ItemKits.FirstOrDefaultAsync(k => k.KitBarcode == barcode.Trim());
            if (kit == null || !await _db.Products.AnyAsync(p => p.Id == kit.ParentProductId && p.IsActive)) return null;

            var kitItems = await _db.ItemKitItems.Where(ki => ki.ItemKitId == kit.Id).ToListAsync();
            var products = await _db.Products.ToDictionaryAsync(p => p.Id, p => p.Name);

            var items = kitItems
                .Select(ki => new ItemKitItemDto(
                    ki.Id,
                    ki.ItemKitId,
                    ki.ProductId,
                    products.TryGetValue(ki.ProductId, out var pName) ? pName : "Producto",
                    ki.Quantity
                ))
                .ToList();

            return new ItemKitDto(kit.Id, kit.KitBarcode, kit.Name, kit.Price, kit.Description, items);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error finding item kit by barcode {Barcode}", barcode);
            return null;
        }
    }

    public async Task<bool> SaveAsync(string id, string barcode, string name, decimal price, string description, List<ItemKitItemDto> items)
    {
        await using var _db = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(_db, Ticketfy.Core.Security.PermissionCatalog.InventoryCreate);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(barcode) || price < 0 || items.Count == 0
            || items.Any(i => i.Quantity <= 0 || !double.IsFinite(i.Quantity) || Math.Round(i.Quantity, 3) != i.Quantity || i.ProductId == id))
            throw new InvalidOperationException("El combo requiere nombre, código, precio y componentes válidos.");
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var cleanBarcode = string.IsNullOrWhiteSpace(barcode) ? $"750{Random.Shared.Next(100000000, 999999999)}" : barcode.Trim();
            var cleanName = string.IsNullOrWhiteSpace(name) ? "Combo / Promoción" : name.Trim();

            _db.ChangeTracker.Clear();

            // 1. Sync ProductEntity so it appears in POS Catalog under category "Promociones"
            var product = await _db.Products.FirstOrDefaultAsync(p => (!string.IsNullOrEmpty(id) && p.Id == id) || (p.Barcode != null && p.Barcode == cleanBarcode));
            if (product is not null && !product.IsKit) throw new InvalidOperationException("El código pertenece a un producto individual.");
            var targetId = product?.Id ?? (string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id);

            if (product == null)
            {
                product = new ProductEntity
                {
                    Id = targetId,
                    Barcode = cleanBarcode,
                    Name = cleanName,
                    Price = price,
                    Cost = 0m,
                    Category = "Promociones",
                    Unit = "Pza",
                    Stock = 0,
                    MinStock = 1.0,
                    IsKit = true,
                    CreatedAt = DateTime.Now.ToString("s")
                };
                _db.Products.Add(product);
            }
            else
            {
                await _operations.RequireAsync(_db, Ticketfy.Core.Security.PermissionCatalog.InventoryEdit);
                product.Barcode = cleanBarcode;
                product.Name = cleanName;
                product.Price = price;
                product.Category = "Promociones";
                product.IsKit = true;
                product.StockVersion++;
                _db.Products.Update(product);
            }

            // 2. Sync ItemKitEntity & ItemKitItems
            var existingKit = await _db.ItemKits.FirstOrDefaultAsync(k => k.Id == targetId || k.ParentProductId == targetId || k.KitBarcode == cleanBarcode);
            string actualKitId = existingKit?.Id ?? targetId;

            if (existingKit == null)
            {
                existingKit = new ItemKitEntity
                {
                    Id = actualKitId,
                    ParentProductId = targetId,
                    KitBarcode = cleanBarcode,
                    Name = cleanName,
                    Price = price,
                    Description = description ?? string.Empty,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _db.ItemKits.Add(existingKit);
            }
            else
            {
                existingKit.ParentProductId = targetId;
                existingKit.KitBarcode = cleanBarcode;
                existingKit.Name = cleanName;
                existingKit.Price = price;
                existingKit.Description = description ?? string.Empty;

                var oldItems = await _db.ItemKitItems.Where(ki => ki.ItemKitId == actualKitId).ToListAsync();
                _db.ItemKitItems.RemoveRange(oldItems);
            }

            if (items != null)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.ProductId)) continue;
                    _db.ItemKitItems.Add(new ItemKitItemEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        ItemKitId = actualKitId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity > 0 ? item.Quantity : 1.0
                    });
                }
            }

            foreach (var component in items ?? throw new InvalidOperationException("El kit requiere componentes."))
            {
                if (!await _db.Products.AnyAsync(p => p.Id == component.ProductId && p.IsActive))
                    throw new InvalidOperationException("Componente inexistente o inactivo.");
                if (await ReachesAsync(_db, component.ProductId, targetId, new HashSet<string>()))
                    throw new InvalidOperationException("El combo produciría un ciclo de componentes.");
            }
            CommercialLedger.Journal(_db, actor, Guid.NewGuid().ToString("N"), "KIT_SAVED", targetId, 0, "Composición de combo");
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            Log.Information("Saved ItemKit {Name} ({Barcode}) with {Count} items as Product in 'Promociones'", cleanName, cleanBarcode, items?.Count ?? 0);
            return true;
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            Console.WriteLine($"[ItemKitService.SaveAsync EXCEPTION]: {ex}");
            Log.Error(ex, "Error saving ItemKit {Name}", name);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
            .RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.InventoryDelete);
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id && p.IsKit);
        if (product is null) return false;
        product.IsActive = false;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "KIT_INACTIVATED", id, 0, "Baja lógica de combo");
        await db.SaveChangesAsync();
        return true;
    }
    public Task<bool> DeductKitStockAsync(string kitId, double kitQuantity) =>
        throw new InvalidOperationException("El inventario de combos se descuenta únicamente dentro de una venta.");

    private static async Task<bool> ReachesAsync(AppDbContext db, string current, string target, HashSet<string> visited)
    {
        if (current == target) return true;
        if (!visited.Add(current)) return false;
        var kit = await db.ItemKits.AsNoTracking().SingleOrDefaultAsync(k => k.ParentProductId == current || k.Id == current);
        if (kit is null) return false;
        foreach (var item in await db.ItemKitItems.AsNoTracking().Where(i => i.ItemKitId == kit.Id).ToListAsync())
            if (await ReachesAsync(db, item.ProductId, target, visited)) return true;
        return false;
    }
}
