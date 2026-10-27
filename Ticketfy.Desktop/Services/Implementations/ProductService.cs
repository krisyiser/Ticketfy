using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Operations;

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
    private Task<OperationIdentity> AuthorizeReadAsync(AppDbContext db) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, PermissionCatalog.InventoryView, false);
    public async Task<ProductEditorOptions> GetEditorOptionsAsync(string? productId = null, string? supplierSearch = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db,
            productId is null ? PermissionCatalog.InventoryCreate : PermissionCatalog.InventoryEdit, false);
        string? assignedId = null;
        if (productId is not null)
        {
            var product = await db.Products.AsNoTracking().Where(p => p.Id == productId)
                .Select(p => new { p.DefaultSupplierId }).SingleOrDefaultAsync()
                ?? throw new InvalidOperationException("Producto inexistente.");
            assignedId = product.DefaultSupplierId;
        }
        // An inventory editor needs a supplier identity, not its RFC, contacts or address.
        var query = db.Suppliers.AsNoTracking().Where(s => s.IsActive == 1);
        if (!string.IsNullOrWhiteSpace(supplierSearch))
        {
            string search = supplierSearch.Trim().ToLowerInvariant();
            query = query.Where(s => s.Name.ToLower().Contains(search) || s.Id == supplierSearch.Trim());
        }
        var suppliers = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).Take(101)
            .Select(s => new ProductSupplierOption(s.Id, s.Name, true)).ToListAsync();
        var assigned = assignedId is null ? null : await db.Suppliers.AsNoTracking().Where(s => s.Id == assignedId)
            .Select(s => new ProductSupplierOption(s.Id, s.Name, s.IsActive == 1)).SingleOrDefaultAsync();
        var categories = await db.Categories.AsNoTracking().Select(c => c.Name).Distinct().OrderBy(name => name).ToListAsync();
        return new(categories, new(suppliers.Take(100).ToList(), suppliers.Count > 100), assigned);
    }
    public async Task<double> GetDefaultMinStockAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        string? raw = await db.Settings.Where(s => s.Key == "Inventory.DefaultMinStock").Select(s => s.Value).SingleOrDefaultAsync();
        return double.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value >= 0 ? value : 5d;
    }
    public async Task SetDefaultMinStockAsync(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1_000_000 || Math.Round(value, 3) != value)
            throw new InvalidOperationException("El mínimo debe estar entre 0 y 1,000,000 con hasta tres decimales.");
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.InventoryStockAdjust, false);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == "Inventory.DefaultMinStock");
        if (setting is null) { setting = new SettingEntity { Key = "Inventory.DefaultMinStock" }; db.Settings.Add(setting); }
        setting.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "INVENTORY_MINIMUM_CHANGED", setting.Key, 0m, "Mínimo predeterminado: " + setting.Value);
        await db.SaveChangesAsync();
    }
    public async Task<List<ProductDto>> GetAllAsync()
    {
        var page = await GetPageAsync(0, LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(page.HasMore);
        return page.Items;
    }
    public async Task<ProductCatalogPage> GetPageAsync(int page, int size = 60, string? search = null, string? category = null)
        => await GetCatalogPageAsync(page, size, search, category, false, null);
    public async Task<ProductCatalogPage> GetPurchasePageAsync(int page, int size = 100, string? search = null, string? supplierId = null)
        => await GetCatalogPageAsync(page, size, search, null, true, supplierId);
    private async Task<ProductCatalogPage> GetCatalogPageAsync(int page, int size, string? search, string? category, bool physicalOnly, string? supplierId)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var query = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (physicalOnly) query = query.Where(p => !p.IsKit);
        if (supplierId is not null) query = query.Where(p => p.DefaultSupplierId == supplierId);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string value = search.Trim().ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(value) || (p.Barcode != null && p.Barcode.ToLower().Contains(value)));
        }
        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(checked(page * size)).Take(size + 1).ToListAsync();
        return new(await MapAvailableAsync(db, rows.Take(size).ToList()), rows.Count > size);
    }
    public async Task<InventoryCatalogPage> GetInventoryPageAsync(int page, int size = 100, string? search = null, bool lowStockOnly = false)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var query = await InventoryQueryAsync(db, search, lowStockOnly);
        bool costsVisible = await SensitiveReadAccess.CanSeeCostsAsync(db, _operations);
        // Stream scalar values: SQLite cannot safely aggregate converted money multiplied by fractional stock.
        decimal stockCost = 0m, stockSale = 0m;
        await foreach (var p in query.Where(p => !p.IsKit).Select(p => new { Cost = costsVisible ? p.Cost : 0m, p.Price, p.Stock }).AsAsyncEnumerable())
        {
            stockCost += p.Cost * (decimal)p.Stock;
            stockSale += p.Price * (decimal)p.Stock;
        }
        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(checked(page * size)).Take(size + 1).ToListAsync();
        return new(await MapAvailableAsync(db, rows.Take(size).ToList()), rows.Count > size, stockCost, stockSale, costsVisible);
    }

    public async Task<List<ProductDto>> GetInventoryChecklistAsync(string? search = null, bool lowStockOnly = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var query = await InventoryQueryAsync(db, search, lowStockOnly);
        var rows = await query.Where(p => !p.IsKit).OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Take(InventoryWorkloadLimits.PrintableRows + 1).ToListAsync();
        if (rows.Count > InventoryWorkloadLimits.PrintableRows)
            throw new InvalidOperationException($"El filtro supera {InventoryWorkloadLimits.PrintableRows} productos físicos. Acote la búsqueda por nombre, código o categoría antes de imprimir.");
        return await MapAvailableAsync(db, rows);
    }

    private static async Task<IQueryable<ProductEntity>> InventoryQueryAsync(AppDbContext db, string? search, bool lowStockOnly)
    {
        var query = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(term) || (p.Barcode != null && p.Barcode.ToLower().Contains(term))
                || (p.Category != null && p.Category.ToLower().Contains(term)));
        }
        if (lowStockOnly)
        {
            string? raw = await db.Settings.Where(s => s.Key == "Inventory.DefaultMinStock").Select(s => s.Value).SingleOrDefaultAsync();
            double minimum = double.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value >= 0 ? value : 5d;
            query = query.Where(p => !p.IsKit && p.Stock <= (p.MinStock > 0 ? p.MinStock : minimum));
        }
        return query;
    }
    public async Task<List<ProductCategoryCount>> GetCategoryCountsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        return await db.Products.AsNoTracking().Where(p => p.IsActive).GroupBy(p => p.Category).OrderBy(g => g.Key)
            .Select(g => new ProductCategoryCount(g.Key ?? "General", g.Count())).ToListAsync();
    }
    public async Task<List<ProductDto>> GetByCategoryAsync(string category)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var rows = await db.Products.AsNoTracking().Where(p => p.IsActive && p.Category == category)
            .OrderBy(p => p.Name).ThenBy(p => p.Id).Take(LegacyCatalogReadPolicy.Limit + 1).ToListAsync();
        LegacyCatalogReadPolicy.RequireComplete(rows.Count > LegacyCatalogReadPolicy.Limit);
        return await MapAvailableAsync(db, rows);
    }
    public async Task<ProductDto?> GetByBarcodeAsync(string barcode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.IsActive && p.Barcode == barcode);
        return p is null ? null : (await MapAvailableAsync(db, [p])).Single();
    }
    public async Task<ProductDto?> GetByIdAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeReadAsync(db);
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        return p is null ? null : (await MapAvailableAsync(db, [p])).Single();
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
        dto = Normalize(dto);
        Validate(dto);
        string id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id;
        if (await db.Products.AnyAsync(p => p.Barcode == dto.Barcode && p.Id != id))
            throw new InvalidOperationException("El código de barras ya está asignado.");
        var entity = await db.Products.SingleOrDefaultAsync(p => p.Id == id);
        if (create && entity is not null) throw new InvalidOperationException("El producto ya existe.");
        if (!create && entity is null) throw new InvalidOperationException("Producto inexistente.");
        if ((create && dto.IsKit) || (entity is not null && dto.IsKit != entity.IsKit))
            throw new InvalidOperationException("Los combos se crean con una receta. No convierta productos físicos y combos desde el editor de catálogo.");
        if (dto.DefaultSupplierId is not null && dto.DefaultSupplierId != entity?.DefaultSupplierId
            && !await db.Suppliers.AnyAsync(s => s.Id == dto.DefaultSupplierId && s.IsActive == 1))
            throw new InvalidOperationException("Seleccione un proveedor activo para el producto.");
        if (!await SensitiveReadAccess.CanSeeCostsAsync(db, _operations))
        {
            if (entity is not null && !dto.CostsVisible) dto = dto with { Cost = entity.Cost };
            else if (dto.Cost != (entity?.Cost ?? 0)) throw new UnauthorizedAccessException("Se requiere permiso de costos y utilidad para modificar el costo.");
        }
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
            || !ValidMoney(p.Price) || !ValidMoney(p.Cost) || !ValidMoney(p.WholesalePrice) || p.WholesaleThreshold < 0
            || !ValidMoney(p.TaxRate) || p.TaxRate > 1
            || !ValidQuantity(p.Stock, p.IsBulk) || !ValidQuantity(p.MinStock, p.IsBulk)
            || !ValidQuantity(p.MaxStock, p.IsBulk) || !ValidQuantity(p.ReorderQuantity, p.IsBulk)
            || (p.MaxStock > 0 && p.MaxStock < p.MinStock) || p.PointsRewarded < 0 || !double.IsFinite(p.PointsRewarded))
            throw new InvalidOperationException("Revise código, nombre, precios, unidades, impuestos y niveles de inventario.");
    }
    private static bool ValidMoney(decimal value) => value >= 0 && value <= long.MaxValue / 100m && CommercialLedger.Money(value) == value;
    private static bool ValidQuantity(double value, bool fractional) => double.IsFinite(value) && value >= 0
        && value <= (double)(long.MaxValue / 1000m) && (decimal)value <= long.MaxValue / 1000m
        && Math.Round(value, 3) == value && (fractional || Math.Truncate(value) == value);
    private static ProductDto Normalize(ProductDto product) => product with
    {
        Barcode = product.Barcode?.Trim(),
        Name = product.Name?.Trim() ?? "",
        Unit = product.Unit?.Trim() ?? "",
        Category = product.Category?.Trim() ?? "General",
        DefaultSupplierId = string.IsNullOrWhiteSpace(product.DefaultSupplierId) ? null : product.DefaultSupplierId.Trim()
    };
    public async Task DeleteAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.InventoryDelete);
        var entity = await db.Products.SingleOrDefaultAsync(p => p.Id == id);
        if (entity is null || !entity.IsActive) return;
        entity.IsActive = false;
        entity.StockVersion = checked(entity.StockVersion + 1);
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
        var batch = products.Take(InventoryWorkloadLimits.ImportRows + 1).Select(Normalize).ToList();
        if (batch.Count > InventoryWorkloadLimits.ImportRows)
            throw new InvalidOperationException($"Cada importación admite hasta {InventoryWorkloadLimits.ImportRows} productos. Divida el archivo; no se importó ningún registro.");
        if (batch.GroupBy(p => p.Barcode?.Trim()).Any(group => group.Count() > 1))
            throw new InvalidOperationException("La importación repite códigos de barras. No se importó ningún registro.");
        foreach (var dto in batch) Validate(dto);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.InventoryCreate);
        await AuthorizeAsync(db, PermissionCatalog.InventoryEdit);
        foreach (var dto in batch)
        {
            var existing = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Barcode == dto.Barcode);
            await SaveProductAsync(db, actor, existing is null ? dto : dto with { Id = existing.Id, ExpectedStockVersion = existing.StockVersion, StockAdjustmentReason = "Importación de inventario autorizada" }, existing is null);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        await tx.CommitAsync();
    }
    public async Task<List<ProductDto>> SearchFtsAsync(string query)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeReadAsync(db);
        var q = query.Trim().ToLowerInvariant();
        return await MapAvailableAsync(db, await db.Products.AsNoTracking().Where(p => p.IsActive && (p.Name.ToLower().Contains(q) || (p.Barcode != null && p.Barcode.ToLower().Contains(q)) || p.Category.ToLower().Contains(q)))
            .OrderBy(p => p.Name).Take(150).ToListAsync());
    }
    public async Task<int> ImportFromCsvTextAsync(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent)) return 0;
        if (csvContent.Length > InventoryWorkloadLimits.CsvCharacters)
            throw new InvalidOperationException("El CSV supera el límite de tamaño. No se importó ningún registro.");
        var items = new List<ProductDto>();
        using var reader = new StringReader(csvContent);
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(reader)
        { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",", ";");
        bool isHeader = true;
        while (!parser.EndOfData)
        {
            long lineNumber = parser.LineNumber;
            string[]? parts;
            try { parts = parser.ReadFields(); }
            catch (Microsoft.VisualBasic.FileIO.MalformedLineException)
            { throw new InvalidOperationException($"Fila {lineNumber}: comillas o delimitadores inválidos. No se importó ningún registro."); }
            if (parts is null || parts.Length == 0) continue;

            if (isHeader)
            {
                isHeader = false;
                if (parts[0].Contains("barcode", StringComparison.OrdinalIgnoreCase) || parts[0].Contains("codigo", StringComparison.OrdinalIgnoreCase)) continue;
            }

            if (parts.Length is < 5 or > 7) throw new InvalidOperationException($"Fila {lineNumber}: se requieren código, nombre, costo, precio y stock, opcionalmente categoría y unidad. No se importó ningún registro.");
            if (items.Count >= InventoryWorkloadLimits.ImportRows)
                throw new InvalidOperationException($"Cada importación admite hasta {InventoryWorkloadLimits.ImportRows} productos. No se importó ningún registro.");
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
        using var reader = File.OpenText(filePath);
        var text = await InventoryWorkloadLimits.ReadCsvAsync(reader);
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
        var page = await GetKardexPageAsync(productId, size: CommercialLedgerReader.LegacyLimit);
        CommercialLedgerReader.RequireComplete(page.HasMore);
        return page.Entries;
    }
    public async Task<InventoryLedgerPageDto> GetKardexPageAsync(string productId, int page = 0, int size = 100, LedgerReadCut? cut = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.InventoryView, false);
        return await CommercialLedgerReader.InventoryAsync(db, productId, page, size, cut, await SensitiveReadAccess.CanSeeCostsAsync(db, _operations));
    }
    // Internal callers must authorize and own the snapshot, as public catalog reads do.
    internal async Task<List<ProductDto>> MapAvailableAsync(AppDbContext db, List<ProductEntity> products)
    {
        bool visible = await SensitiveReadAccess.CanSeeCostsAsync(db, _operations);
        var availability = await CatalogKitAvailability.CalculateAsync(db, products);
        return products.Select(p =>
        {
            var dto = Map(p) with { Cost = visible ? p.Cost : 0m, CostsVisible = visible };
            return p.IsKit ? dto with { Stock = availability[p.Id].Stock, InventoryWarning = availability[p.Id].Warning } : dto;
        }).ToList();
    }
    private static ProductDto Map(ProductEntity p) => new(p.Id, p.Barcode, p.Name, p.Cost, p.Price, p.WholesalePrice,
        p.WholesaleThreshold, p.Stock, p.Category, p.Unit, p.ExpiresSoon, p.CreatedAt, p.PointsRewarded, p.ReorderQuantity,
        p.LocationRack, p.SatProductCode, p.SatUnitCode, p.MinStock, p.DefaultSupplierId, p.IsBulk, p.IsKit, p.IsActive, p.MaxStock, p.TaxRate,
        ExpectedStockVersion: p.StockVersion);
}
