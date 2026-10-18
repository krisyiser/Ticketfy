using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Ticketfy.Data.Entities;
using System.IO;

namespace Ticketfy.Services.Implementations;

public class SalesVelocityModel
{
    public string Name { get; set; } = string.Empty;
    public double Stock { get; set; }
    public decimal TotalSold28Days { get; set; }
}

public class PredictiveIntelligenceService : IPredictiveIntelligenceService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public PredictiveIntelligenceService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<ProductDto?> GetTopCorrelatedProductAsync(string sourceProductId, List<string> currentCartProductIds)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        // Buscar correlaciones, excluyendo lo que YA está en el carrito
        var topMatch = await context.Set<CoOccurrenceEntity>()
            .Where(c => c.ProductoA == sourceProductId && !currentCartProductIds.Contains(c.ProductoB))
            .OrderByDescending(c => c.Frecuencia)
            .Select(c => c.ProductoB)
            .FirstOrDefaultAsync();

        if (string.IsNullOrEmpty(topMatch)) return null;

        var product = await context.Products.FindAsync(topMatch);
        if (product != null && product.Stock > 0)
        {
            return new ProductDto(product.Id, product.Barcode, product.Name, product.Cost, product.Price, product.WholesalePrice, product.WholesaleThreshold, product.Stock, product.Category, product.Unit, product.ExpiresSoon, product.CreatedAt, product.PointsRewarded, product.ReorderQuantity, product.LocationRack, product.SatProductCode, product.SatUnitCode, product.MinStock);
        }
        return null;
    }

    public async Task<List<PredictiveAlertDto>> GetUrgentRestockAlertsAsync()
    {
        var thresholdDate = DateTime.Now.AddDays(-28).ToString("o");
        using var context = await _contextFactory.CreateDbContextAsync();

        var recentSales = await context.Sales
            .Where(s => string.Compare(s.Date, thresholdDate) >= 0 && s.Status == Ticketfy.Core.Enums.SaleStatus.Completed)
            .Select(s => s.ItemsJson)
            .ToListAsync();

        var productSales = new Dictionary<string, decimal>();
        foreach (var json in recentSales)
        {
            try
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<Ticketfy.Data.Dtos.SaleItemSnapshotDto>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (!productSales.ContainsKey(item.ProductId)) productSales[item.ProductId] = 0;
                        productSales[item.ProductId] += (decimal)item.Quantity;
                    }
                }
            }
            catch { }
        }

        var alerts = new List<PredictiveAlertDto>();
        if (productSales.Count > 0)
        {
            var productIds = productSales.Keys.ToList();
            var products = await context.Products.Where(p => productIds.Contains(p.Id)).ToListAsync();
            foreach (var p in products)
            {
                var totalSold28Days = productSales[p.Id];
                decimal dailyVelocity = totalSold28Days / 28m;
                if (dailyVelocity <= 0) continue;

                decimal daysRemaining = (decimal)p.Stock / dailyVelocity;

                if (daysRemaining <= 3.0m) // Límite crítico de 3 días
                {
                    alerts.Add(new PredictiveAlertDto
                    {
                        Message = $"URGENTE: Te quedarás sin {p.Name} en {Math.Round(daysRemaining, 1)} días a tu ritmo de ventas actual. ¡Pide al proveedor hoy!",
                        DaysRemaining = daysRemaining
                    });
                }
            }
        }

        return alerts.OrderBy(a => a.DaysRemaining).ToList();
    }
}
