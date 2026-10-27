using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Partial extension of SaleService handling recursive stock deductions, kit components,
/// low stock alerts, discount proration, and folio sequence generation.
/// </summary>
public partial class SaleService
{
    private async Task<string> GenerateNextSaleFolioAsync(AppDbContext _ctx)
    {
        string datePrefix = Ticketfy.Core.Helpers.BusinessTime.Now.ToString("ddMMyy");

        var query = @"
            INSERT INTO FolioSequences (DatePrefix, LastSequence) 
            VALUES (@p0, 1) 
            ON CONFLICT(DatePrefix) 
            DO UPDATE SET LastSequence = LastSequence + 1 
            RETURNING LastSequence;";

        var connection = _ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (_ctx.Database.CurrentTransaction != null)
            command.Transaction = _ctx.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = query;

        var param = command.CreateParameter();
        param.ParameterName = "@p0";
        param.Value = datePrefix;
        command.Parameters.Add(param);

        var result = await command.ExecuteScalarAsync();
        int sequence = Convert.ToInt32(result);

        return $"{datePrefix}-{sequence:D4}";
    }

    private async Task<string> GenerateNextReturnFolioAsync(AppDbContext _ctx)
    {
        string datePrefix = $"DEV-{Ticketfy.Core.Helpers.BusinessTime.Now.ToString("ddMMyy")}";

        var query = @"
            INSERT INTO FolioSequences (DatePrefix, LastSequence) 
            VALUES (@p0, 1) 
            ON CONFLICT(DatePrefix) 
            DO UPDATE SET LastSequence = LastSequence + 1 
            RETURNING LastSequence;";

        var connection = _ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (_ctx.Database.CurrentTransaction != null)
            command.Transaction = _ctx.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = query;

        var param = command.CreateParameter();
        param.ParameterName = "@p0";
        param.Value = datePrefix;
        command.Parameters.Add(param);

        var result = await command.ExecuteScalarAsync();
        int sequence = Convert.ToInt32(result);

        return $"{datePrefix}-{sequence:D4}";
    }

    private async Task CheckAndFlagLowStockAsync(AppDbContext _ctx, ProductEntity product)
    {
        if (product.Stock <= product.MinStock)
        {
            bool alertExists = _ctx.SystemAlerts.Local.Any(a => a.ProductId == product.Id && !a.IsResolved)
                || await _ctx.SystemAlerts.AnyAsync(a => a.ProductId == product.Id && !a.IsResolved);

            if (!alertExists)
            {
                var alert = new SystemAlertEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    ProductId = product.Id,
                    SupplierId = product.DefaultSupplierId,
                    Title = $"Stock Crítico: {product.Name}",
                    Message = $"Stock actual ({product.Stock}) por debajo del mínimo permitido ({product.MinStock}).",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    IsResolved = false
                };
                _ctx.SystemAlerts.Add(alert);
            }
        }
    }

    private async Task<decimal> DeductInventoryAsync(AppDbContext db, KitInventoryGraph graph, string productId, decimal quantity, OperationIdentity actor, string operationId, string documentId, string saleLineId)
    {
        var plan = graph.RequirePlan(productId);
        // Aggregate repeated/nested leaves first; quantities and costs never multiply as double.
        var requirements = graph.QuantitiesFor(plan, quantity);
        foreach (var pair in requirements.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            await CommercialLedger.StockAsync(db, actor, operationId, documentId, pair.Key, -pair.Value, "SALE", saleLineId);
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == pair.Key);
            await CheckAndFlagLowStockAsync(db, product);
        }
        return plan.UnitCost;
    }

    private static List<SaleItemSnapshotDto> ApplyProratedGlobalDiscountAndTaxes(List<SaleItemSnapshotDto> items, decimal globalDiscountAmount)
    {
        decimal subtotal = items.Sum(i => i.TotalPrice);
        if (globalDiscountAmount > subtotal) throw new InvalidOperationException("El descuento supera el subtotal.");
        decimal cumulative = 0m, allocated = 0m;
        var result = new List<SaleItemSnapshotDto>();
        foreach (var item in items)
        {
            cumulative += item.TotalPrice;
            decimal target = subtotal == 0 ? 0m : CommercialLedger.Money(globalDiscountAmount * cumulative / subtotal);
            decimal discount = target - allocated;
            allocated = target;
            result.Add(item with { ProratedGlobalDiscountAmount = discount, TotalPrice = item.TotalPrice - discount, TaxAmount = 0m });
        }
        return result;
    }
}
