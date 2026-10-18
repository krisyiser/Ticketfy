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
    private readonly AppDbContext _db;

    public ItemKitService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ItemKitDto>> GetAllAsync()
    {
        try
        {
            var kits = await _db.ItemKits.ToListAsync();
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
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        try
        {
            var kit = await _db.ItemKits.FirstOrDefaultAsync(k => k.KitBarcode == barcode.Trim());
            if (kit == null) return null;

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
        try
        {
            var cleanBarcode = string.IsNullOrWhiteSpace(barcode) ? $"750{Random.Shared.Next(100000000, 999999999)}" : barcode.Trim();
            var cleanName = string.IsNullOrWhiteSpace(name) ? "Combo / Promoción" : name.Trim();

            _db.ChangeTracker.Clear();

            // 1. Sync ProductEntity so it appears in POS Catalog under category "Promociones"
            var product = await _db.Products.FirstOrDefaultAsync(p => (!string.IsNullOrEmpty(id) && p.Id == id) || (p.Barcode != null && p.Barcode == cleanBarcode));
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
                    Stock = 9999.0,
                    MinStock = 1.0,
                    IsKit = true,
                    CreatedAt = DateTime.Now.ToString("s")
                };
                _db.Products.Add(product);
            }
            else
            {
                product.Barcode = cleanBarcode;
                product.Name = cleanName;
                product.Price = price;
                product.Category = "Promociones";
                product.IsKit = true;
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

            // 3. Sync PromotionEntity for unified Promotions module tracking
            var promo = await _db.Promotions.FirstOrDefaultAsync(p => p.Id == targetId || p.TargetProductId == targetId);
            if (promo == null)
            {
                _db.Promotions.Add(new PromotionEntity
                {
                    Id = targetId,
                    Name = cleanName,
                    Type = "product",
                    StrategyType = Ticketfy.Core.Enums.PromotionType.FixedAmountDiscount,
                    TargetProductId = targetId,
                    DiscountType = "fixed",
                    DiscountValue = price,
                    IsActive = 1,
                    StartDate = DateTime.Now.AddDays(-1).ToString("s"),
                    EndDate = DateTime.Now.AddYears(5).ToString("s")
                });
            }
            else
            {
                promo.Name = cleanName;
                promo.DiscountValue = price;
                promo.IsActive = 1;
                promo.TargetProductId = targetId;
                _db.Promotions.Update(promo);
            }

            await _db.SaveChangesAsync();
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
        try
        {
            var kit = await _db.ItemKits.FirstOrDefaultAsync(k => k.Id == id || k.ParentProductId == id);
            if (kit != null)
            {
                var items = await _db.ItemKitItems.Where(ki => ki.ItemKitId == kit.Id).ToListAsync();
                _db.ItemKitItems.RemoveRange(items);
                _db.ItemKits.Remove(kit);
            }

            var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == id);
            if (product != null) _db.Products.Remove(product);

            var promo = await _db.Promotions.FirstOrDefaultAsync(p => p.Id == id || p.TargetProductId == id);
            if (promo != null) _db.Promotions.Remove(promo);

            await _db.SaveChangesAsync();
            Log.Information("Deleted ItemKit and linked Product/Promotion {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting ItemKit {Id}", id);
            return false;
        }
    }

    public async Task<bool> DeductKitStockAsync(string kitId, double kitQuantity)
    {
        try
        {
            var kitItems = await _db.ItemKitItems.Where(ki => ki.ItemKitId == kitId).ToListAsync();
            foreach (var item in kitItems)
            {
                var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId);
                if (product != null)
                {
                    double totalDeduction = item.Quantity * kitQuantity;
                    product.Stock = Math.Max(0, product.Stock - totalDeduction);
                    Log.Information("Deducted {Qty} of Product {Name} for Kit Sale", totalDeduction, product.Name);
                }
            }
            await _db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deducting stock for ItemKit {KitId}", kitId);
            return false;
        }
    }
}
