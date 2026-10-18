using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class ProductService : IProductService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ProductService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ProductService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db, string permission) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, permission);
    public async Task<List<ProductDto>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var result = (await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync()).Select(Map).ToList();
        for (int i = 0; i < result.Count; i++) if (result[i].IsKit)
            result[i] = result[i] with { Stock = await KitAvailabilityAsync(db, result[i].Id, new HashSet<string>()) };
        return result;
    }
    public async Task<List<ProductDto>> GetByCategoryAsync(string category)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await MapAvailableAsync(db, await db.Products.AsNoTracking().Where(p => p.IsActive && p.Category == category).OrderBy(p => p.Name).ToListAsync());
    }
    public async Task<ProductDto?> GetByBarcodeAsync(string barcode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.IsActive && p.Barcode == barcode);
        return p is null ? null : p.IsKit ? Map(p) with { Stock = await KitAvailabilityAsync(db, p.Id, new HashSet<string>()) } : Map(p);
    }
    public async Task<ProductDto?> GetByIdAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        return p is null ? null : p.IsKit ? Map(p) with { Stock = await KitAvailabilityAsync(db, p.Id, new HashSet<string>()) } : Map(p);
    }
    public Task AddAsync(ProductDto product) => SaveAsync(product, true);
    public Task UpdateAsync(ProductDto product) => SaveAsync(product, false);
    private async Task SaveAsync(ProductDto dto, bool create)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, create ? PermissionCatalog.InventoryCreate : PermissionCatalog.InventoryEdit);
        await SaveProductAsync(db, actor, dto, create);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private async Task SaveProductAsync(AppDbContext db, OperationIdentity actor, ProductDto dto, bool create)
    {
        Validate(dto);
        string id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id;
        if (await db.Products.AnyAsync(p => p.Barcode == dto.Barcode && p.Id != id))
            throw new InvalidOperationException("El código de barras ya está asignado.");
        var entity = await db.Products.SingleOrDefaultAsync(p => p.Id == id);
        if (create && entity is not null) throw new InvalidOperationException("El producto ya existe.");
        if (!create && entity is null) throw new InvalidOperationException("Producto inexistente.");
        if (!create && (dto.ExpectedStockVersion is null || dto.ExpectedStockVersion != entity!.StockVersion))
            throw new DbUpdateConcurrencyException("El producto o sus existencias cambiaron. Recargue el registro antes de editarlo.");
        if (entity is null)
        {
            entity = new ProductEntity { Id = id, Barcode = dto.Barcode, Name = dto.Name.Trim(), Stock = 0, CreatedAt = DateTimeOffset.UtcNow.ToString("O") };
            db.Products.Add(entity);
            await db.SaveChangesAsync();
        }
        string operation = Guid.NewGuid().ToString("N");
        decimal delta = dto.IsKit ? 0m : (decimal)dto.Stock - (decimal)entity.Stock;
        if (delta != 0)
        {
            await AuthorizeAsync(db, PermissionCatalog.InventoryStockAdjust);
            string reason = create ? "Existencia inicial" : dto.StockAdjustmentReason ?? "";
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Indique el motivo del ajuste de existencias.");
            await CommercialLedger.StockAsync(db, actor, operation, id, id, delta, reason, cost: dto.Cost);
            await db.Entry(entity).ReloadAsync();
        }
        entity.Barcode = dto.Barcode?.Trim(); entity.Name = dto.Name.Trim(); entity.Cost = dto.Cost; entity.Price = dto.Price;
        entity.WholesalePrice = dto.WholesalePrice; entity.WholesaleThreshold = dto.WholesaleThreshold; entity.Category = dto.Category;
        entity.Unit = dto.Unit; entity.MinStock = dto.MinStock; entity.MaxStock = dto.MaxStock; entity.IsActive = dto.IsActive;
        entity.IsBulk = dto.IsBulk; entity.IsKit = dto.IsKit; entity.ReorderQuantity = dto.ReorderQuantity; entity.LocationRack = dto.LocationRack;
        entity.SatProductCode = dto.SatProductCode; entity.SatUnitCode = dto.SatUnitCode; entity.TaxRate = dto.TaxRate;
        entity.DefaultSupplierId = dto.DefaultSupplierId; entity.PointsRewarded = dto.PointsRewarded; entity.ExpiresSoon = dto.ExpiresSoon;
        entity.StockVersion++;
        CommercialLedger.Journal(db, actor, operation, create ? "PRODUCT_CREATED" : "PRODUCT_UPDATED", id, 0, dto.StockAdjustmentReason ?? "Catálogo");
    }
    private static void Validate(ProductDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Barcode) || string.IsNullOrWhiteSpace(p.Unit)
            || p.Price < 0 || p.Cost < 0 || p.WholesalePrice < 0 || p.WholesaleThreshold < 0 || p.TaxRate < 0 || p.TaxRate > 1
            || CommercialLedger.Money(p.Price) != p.Price || CommercialLedger.Money(p.Cost) != p.Cost
            || !double.IsFinite(p.Stock) || p.Stock < 0 || Math.Round(p.Stock, 3) != p.Stock || (!p.IsBulk && Math.Truncate(p.Stock) != p.Stock)
            || !double.IsFinite(p.MinStock) || !double.IsFinite(p.MaxStock) || p.MinStock < 0 || p.MaxStock < 0
            || (p.MaxStock > 0 && p.MaxStock < p.MinStock) || p.PointsRewarded < 0 || !double.IsFinite(p.PointsRewarded))
            throw new InvalidOperationException("Revise código, nombre, precios, unidades, impuestos y niveles de inventario.");
    }
    public async Task DeleteAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.InventoryDelete);
        var entity = await db.Products.SingleOrDefaultAsync(p => p.Id == id);
        if (entity is null || !entity.IsActive) return;
        entity.IsActive = false;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "PRODUCT_INACTIVATED", id, 0, "Baja lógica; conserva existencias e historial");
        await db.SaveChangesAsync();
    }
    public async Task ClearInventoryAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.InventoryDelete);
        var products = await db.Products.Where(p => p.IsActive).ToListAsync();
        foreach (var p in products) p.IsActive = false;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "CATALOG_INACTIVATED", "CATALOG", 0, $"Productos: {products.Count}");
        await db.SaveChangesAsync();
    }
    public async Task BulkSaveAsync(IEnumerable<ProductDto> products)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.InventoryCreate);
        await AuthorizeAsync(db, PermissionCatalog.InventoryEdit);
        foreach (var dto in products)
        {
            var existing = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Barcode == dto.Barcode);
            await SaveProductAsync(db, actor, existing is null ? dto : dto with { Id = existing.Id, ExpectedStockVersion = existing.StockVersion, StockAdjustmentReason = "Importación de inventario autorizada" }, existing is null);
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }
    public async Task<List<ProductDto>> SearchFtsAsync(string query)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = query.Trim();
        return await MapAvailableAsync(db, await db.Products.AsNoTracking().Where(p => p.IsActive && (p.Name.Contains(q) || (p.Barcode != null && p.Barcode.Contains(q)) || p.Category.Contains(q)))
            .OrderBy(p => p.Name).Take(150).ToListAsync());
    }
    public async Task<int> ImportFromCsvTextAsync(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent)) return 0;
        var items = new List<ProductDto>();
        using var reader = new StringReader(csvContent);
        string? line;
        bool isHeader = true;
        int lineNumber = 0;

        var csvParser = new System.Text.RegularExpressions.Regex("[,;](?=(?:[^\"]*\"[^\"]*\")*(?![^\"]*\"))");

        while ((line = await reader.ReadLineAsync()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = csvParser.Split(line);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim().TrimStart('"').TrimEnd('"');
            }

            if (isHeader)
            {
                isHeader = false;
                if (parts[0].Contains("barcode", StringComparison.OrdinalIgnoreCase) || parts[0].Contains("codigo", StringComparison.OrdinalIgnoreCase)) continue;
            }

            if (parts.Length < 5) throw new InvalidOperationException($"Fila {lineNumber}: se requieren código, nombre, costo, precio y stock. No se importó ningún registro.");
            string barcode = parts[0];
            string name = parts[1];
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign;
            if (!decimal.TryParse(parts[2], style, culture, out decimal cost)
                || !decimal.TryParse(parts[3], style, culture, out decimal price)
                || !double.TryParse(parts[4], style, culture, out double stock))
                throw new InvalidOperationException($"Fila {lineNumber}: importes inválidos; utilice punto decimal y no separadores de miles. No se importó ningún registro.");
            string category = parts.Length > 5 ? parts[5] : "General";
            string unit = parts.Length > 6 ? parts[6] : "pza";
            bool isBulk = unit.Equals("kg", StringComparison.OrdinalIgnoreCase) || unit.Equals("lt", StringComparison.OrdinalIgnoreCase) || unit.Equals("gr", StringComparison.OrdinalIgnoreCase);

            items.Add(new ProductDto(Guid.NewGuid().ToString(), barcode, name, cost, price, Stock: stock, Category: category, Unit: unit, IsBulk: isBulk));
        }

        if (items.GroupBy(p => p.Barcode).Any(g => g.Count() > 1)) throw new InvalidOperationException("El archivo repite códigos de barras. No se importó ningún registro.");
        if (items.Count > 0) await BulkSaveAsync(items);
        return items.Count;
    }

    public async Task<int> ImportCsvAsync(string filePath)
    {
        if (!File.Exists(filePath)) return 0;
        var text = await File.ReadAllTextAsync(filePath);
        return await ImportFromCsvTextAsync(text);
    }


    public async Task<bool> AdjustStockManuallyAsync(string productId, double newPhysicalStock, string reason, string userId,
        Ticketfy.Services.Security.ISecurityInterceptionService? securityService = null, IAuditService? auditService = null)
    {
        var product = await GetByIdAsync(productId) ?? throw new InvalidOperationException("Producto inexistente.");
        await UpdateAsync(product with { Stock = newPhysicalStock, StockAdjustmentReason = reason });
        return true;
    }
    public async Task<IEnumerable<ProductDto>> GetCatalogForPosAsync() => await GetAllAsync();
    public async Task<List<InventoryMovementEntity>> GetKardexAsync(string productId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.InventoryView, false);
        return await db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.CreatedAtUtc).ToListAsync();
    }
    private static async Task<double> KitAvailabilityAsync(AppDbContext db, string id, HashSet<string> visited)
    {
        var requirements = new Dictionary<string, decimal>();
        async Task<bool> ExpandAsync(string productId, decimal multiplier)
        {
            if (!visited.Add(productId)) return false;
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId && p.IsActive);
            if (product is null) return false;
            if (!product.IsKit) requirements[productId] = requirements.GetValueOrDefault(productId) + multiplier;
            else
            {
                var kit = await db.ItemKits.AsNoTracking().SingleOrDefaultAsync(k => k.Id == productId || k.ParentProductId == productId);
                if (kit is null) return false;
                var components = await db.ItemKitItems.AsNoTracking().Where(i => i.ItemKitId == kit.Id).ToListAsync();
                if (components.Count == 0) return false;
                foreach (var part in components)
                    if (!double.IsFinite(part.Quantity) || part.Quantity <= 0 || !await ExpandAsync(part.ProductId, multiplier * (decimal)part.Quantity)) return false;
            }
            visited.Remove(productId);
            return true;
        }
        if (!await ExpandAsync(id, 1m) || requirements.Count == 0) return 0;
        decimal available = decimal.MaxValue;
        foreach (var requirement in requirements)
        {
            var stock = await db.Products.AsNoTracking().Where(p => p.Id == requirement.Key).Select(p => p.Stock).SingleAsync();
            available = Math.Min(available, decimal.Floor((decimal)stock / requirement.Value));
        }
        return (double)Math.Max(0, available);
    }
    private static async Task<List<ProductDto>> MapAvailableAsync(AppDbContext db, List<ProductEntity> products)
    {
        var result = products.Select(Map).ToList();
        for (int i = 0; i < result.Count; i++) if (result[i].IsKit)
            result[i] = result[i] with { Stock = await KitAvailabilityAsync(db, result[i].Id, new HashSet<string>()) };
        return result;
    }
    private static ProductDto Map(ProductEntity p) => new(p.Id, p.Barcode, p.Name, p.Cost, p.Price, p.WholesalePrice,
        p.WholesaleThreshold, p.Stock, p.Category, p.Unit, p.ExpiresSoon, p.CreatedAt, p.PointsRewarded, p.ReorderQuantity,
        p.LocationRack, p.SatProductCode, p.SatUnitCode, p.MinStock, p.DefaultSupplierId, p.IsBulk, p.IsKit, p.IsActive, p.MaxStock, p.TaxRate,
        ExpectedStockVersion: p.StockVersion);
}
