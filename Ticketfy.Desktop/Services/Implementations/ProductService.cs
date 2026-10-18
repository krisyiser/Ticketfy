using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Services.Implementations;

public sealed class ProductService : IProductService
{
    private readonly AppDbContext _ctx;

    public ProductService(AppDbContext ctx) => _ctx = ctx;

    public async Task<List<ProductDto>> GetAllAsync()
    {
        var list = await _ctx.Products
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();
        return list.Select(MapToDto).ToList();
    }

    public async Task<List<ProductDto>> GetByCategoryAsync(string category)
    {
        var list = await _ctx.Products
            .AsNoTracking()
            .Where(p => p.Category == category)
            .OrderBy(p => p.Name)
            .ToListAsync();
        return list.Select(MapToDto).ToList();
    }

    public async Task<ProductDto?> GetByBarcodeAsync(string barcode)
    {
        var entity = await _ctx.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Barcode == barcode);
        return entity is null ? null : MapToDto(entity);
    }

    public async Task<ProductDto?> GetByIdAsync(string id)
    {
        var entity = await _ctx.Products.FindAsync(id);
        return entity is null ? null : MapToDto(entity);
    }

    public async Task AddAsync(ProductDto product)
    {
        _ctx.ChangeTracker.Clear();

        if (!string.IsNullOrWhiteSpace(product.Barcode))
        {
            var existing = await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Barcode == product.Barcode);
            if (existing != null)
            {
                throw new InvalidOperationException($"El código de barras '{product.Barcode}' ya está asignado al producto '{existing.Name}'.");
            }
        }

        _ctx.Products.Add(MapToEntity(product));
        await _ctx.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProductDto product)
    {
        _ctx.ChangeTracker.Clear();

        if (!string.IsNullOrWhiteSpace(product.Barcode))
        {
            var existing = await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Barcode == product.Barcode && p.Id != product.Id);
            if (existing != null)
            {
                throw new InvalidOperationException($"El código de barras '{product.Barcode}' ya está asignado al producto '{existing.Name}'.");
            }
        }

        var entity = await _ctx.Products.FindAsync(product.Id);
        if (entity is null) return;

        entity.Barcode = product.Barcode;
        entity.Name = product.Name;
        entity.Cost = product.Cost;
        entity.Price = product.Price;
        entity.WholesalePrice = product.WholesalePrice;
        entity.WholesaleThreshold = product.WholesaleThreshold;
        entity.Stock = product.Stock;
        entity.Category = product.Category;
        entity.Unit = product.Unit;
        entity.ExpiresSoon = product.ExpiresSoon;
        entity.PointsRewarded = product.PointsRewarded;
        entity.ReorderQuantity = product.ReorderQuantity;
        entity.LocationRack = product.LocationRack;
        entity.SatProductCode = product.SatProductCode;
        entity.SatUnitCode = product.SatUnitCode;
        entity.MinStock = product.MinStock;
        entity.DefaultSupplierId = product.DefaultSupplierId;
        entity.IsBulk = product.IsBulk;
        entity.IsKit = product.IsKit;

        _ctx.Products.Update(entity);
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _ctx.Products.FindAsync(id);
        if (entity is not null)
        {
            _ctx.Products.Remove(entity);
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task ClearInventoryAsync()
    {
        await _ctx.Products.ExecuteDeleteAsync();
    }

    public async Task BulkSaveAsync(IEnumerable<ProductDto> products)
    {
        foreach (var p in products)
        {
            var existing = await _ctx.Products
                .FirstOrDefaultAsync(e => e.Barcode == p.Barcode && p.Barcode != null);

            if (existing is null)
            {
                _ctx.Products.Add(MapToEntity(p));
            }
            else
            {
                existing.Name = p.Name;
                existing.Cost = p.Cost;
                existing.Price = p.Price;
                existing.WholesalePrice = p.WholesalePrice;
                existing.WholesaleThreshold = p.WholesaleThreshold;
                existing.Stock = p.Stock;
                existing.Category = p.Category;
                existing.Unit = p.Unit;
                existing.PointsRewarded = p.PointsRewarded;
                existing.ReorderQuantity = p.ReorderQuantity;
                existing.LocationRack = p.LocationRack;
                existing.SatProductCode = p.SatProductCode;
                existing.SatUnitCode = p.SatUnitCode;
                existing.MinStock = p.MinStock;
                existing.IsBulk = p.IsBulk;
                existing.IsKit = p.IsKit;
            }
        }
        await _ctx.SaveChangesAsync();
    }

    public async Task<List<ProductDto>> SearchFtsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return await GetAllAsync();
        var q = query.Trim().ToLower();
        var list = await _ctx.Products
            .AsNoTracking()
            .Where(p => (p.Barcode != null && EF.Functions.Like(p.Barcode.ToLower(), $"%{q}%")) ||
                        EF.Functions.Like(p.Name.ToLower(), $"%{q}%") ||
                        EF.Functions.Like(p.Category.ToLower(), $"%{q}%"))
            .Take(150)
            .ToListAsync();
        return list.Select(MapToDto).ToList();
    }

    public async Task<int> ImportFromCsvTextAsync(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent)) return 0;
        var items = new List<ProductDto>();
        using var reader = new StringReader(csvContent);
        string? line;
        bool isHeader = true;

        var csvParser = new System.Text.RegularExpressions.Regex("[,;](?=(?:[^\"]*\"[^\"]*\")*(?![^\"]*\"))");

        while ((line = await reader.ReadLineAsync()) != null)
        {
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

            if (parts.Length < 4) continue;
            string barcode = parts[0];
            string name = parts[1];
            _ = decimal.TryParse(parts[2], out decimal cost);
            _ = decimal.TryParse(parts[3], out decimal price);
            double stock = parts.Length > 4 && double.TryParse(parts[4], out double s) ? s : 10.0;
            string category = parts.Length > 5 ? parts[5] : "General";
            string unit = parts.Length > 6 ? parts[6] : "pza";
            bool isBulk = unit.Equals("kg", StringComparison.OrdinalIgnoreCase) || unit.Equals("lt", StringComparison.OrdinalIgnoreCase) || unit.Equals("gr", StringComparison.OrdinalIgnoreCase);

            items.Add(new ProductDto(Guid.NewGuid().ToString(), barcode, name, cost, price, Stock: stock, Category: category, Unit: unit, IsBulk: isBulk));
        }

        if (items.Count > 0) await BulkSaveAsync(items);
        return items.Count;
    }

    public async Task<int> ImportCsvAsync(string filePath)
    {
        if (!File.Exists(filePath)) return 0;
        var text = await File.ReadAllTextAsync(filePath);
        return await ImportFromCsvTextAsync(text);
    }

    public async Task<bool> AdjustStockManuallyAsync(string productId, double newPhysicalStock, string reason, string userId, Ticketfy.Services.Security.ISecurityInterceptionService? securityService = null, IAuditService? auditService = null)
    {
        using var transaction = await _ctx.Database.BeginTransactionAsync();
        try
        {
            var product = await _ctx.Products.FindAsync(productId)
                ?? throw new InvalidOperationException($"Producto {productId} no encontrado.");

            double oldStock = product.Stock;
            double deltaStock = newPhysicalStock - oldStock;

            if (Math.Abs(deltaStock) < 0.001) return true;

            decimal financialLoss = Convert.ToDecimal(Math.Abs(deltaStock)) * product.Cost;
            string? supervisorId = null;

            if (deltaStock < 0 && financialLoss >= 100m && securityService != null)
            {
                var auth = await securityService.AuthorizeHighRiskActionAsync(
                    "Ajuste de Merma / Faltante de Stock",
                    $"Pérdida detectada de {Math.Abs(deltaStock):N2} unidades de '{product.Name}'. Impacto: ${financialLoss:N2}");

                if (!auth.IsAuthorized)
                {
                    throw new UnauthorizedAccessException("Ajuste cancelado por falta de autorización del supervisor.");
                }

                supervisorId = auth.SupervisorId;
            }

            product.Stock = newPhysicalStock;
            _ctx.Products.Update(product);

            if (auditService != null)
            {
                await auditService.LogAsync(new AuditLogEntity
                {
                    UserId = userId,
                    AuthorizedBySupervisorId = supervisorId,
                    ActionType = Ticketfy.Core.Enums.AuditActionType.InventoryStockAdjustment,
                    RiskLevel = deltaStock < 0 ? Ticketfy.Core.Enums.RiskLevel.HighRisk : Ticketfy.Core.Enums.RiskLevel.Info,
                    EntityName = nameof(ProductEntity),
                    EntityId = product.Id,
                    OldValue = oldStock.ToString("F2"),
                    NewValue = newPhysicalStock.ToString("F2"),
                    FinancialImpact = deltaStock < 0 ? financialLoss : 0m,
                    Reason = reason
                });
            }

            await _ctx.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    [Dapper.DapperAot]
    public async Task<IEnumerable<ProductDto>> GetCatalogForPosAsync()
    {
        string securePassword = Ticketfy.Services.Security.SecurityManager.GetMasterKey();
        string secureConnectionString = $"Data Source={AppDataPaths.DatabaseFile};Password={securePassword};";

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(secureConnectionString);
        await connection.OpenAsync();

        // Consulta SQL cruda y estricta, mapeada a un DTO por el compilador AOT
        const string sql = "SELECT Id, Barcode, Name, Cost, Price, WholesalePrice, WholesaleThreshold, Stock, Category, Unit, ExpiresSoon, CreatedAt, PointsRewarded, ReorderQuantity, LocationRack, sat_product_code AS SatProductCode, sat_unit_code AS SatUnitCode, MinStock, is_bulk AS IsBulk, is_kit AS IsKit FROM products";
        return await Dapper.SqlMapper.QueryAsync<ProductDto>(connection, sql);
    }

    private static ProductDto MapToDto(ProductEntity e) => new(
        e.Id, e.Barcode, e.Name, e.Cost, e.Price,
        e.WholesalePrice, e.WholesaleThreshold,
        e.Stock, e.Category, e.Unit, e.ExpiresSoon, e.CreatedAt,
        e.PointsRewarded, e.ReorderQuantity, e.LocationRack, e.SatProductCode, e.SatUnitCode, e.MinStock,
        e.DefaultSupplierId, e.IsBulk, e.IsKit);

    private static ProductEntity MapToEntity(ProductDto d) => new()
    {
        Id = string.IsNullOrEmpty(d.Id) ? IdGenerator.NewProductId() : d.Id,
        Barcode = d.Barcode,
        Name = d.Name,
        Cost = d.Cost,
        Price = d.Price,
        WholesalePrice = d.WholesalePrice,
        WholesaleThreshold = d.WholesaleThreshold,
        Stock = d.Stock,
        MinStock = d.MinStock,
        Category = d.Category,
        Unit = d.Unit,
        ExpiresSoon = d.ExpiresSoon,
        CreatedAt = d.CreatedAt ?? DateTimeOffset.Now.ToString("o"),
        PointsRewarded = d.PointsRewarded,
        ReorderQuantity = d.ReorderQuantity,
        LocationRack = d.LocationRack,
        SatProductCode = d.SatProductCode,
        SatUnitCode = d.SatUnitCode,
        DefaultSupplierId = d.DefaultSupplierId,
        IsBulk = d.IsBulk,
        IsKit = d.IsKit
    };

}
