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
        string datePrefix = DateTime.Now.ToString("ddMMyy");

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
        string datePrefix = $"DEV-{DateTime.Now.ToString("ddMMyy")}";

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
            bool alertExists = await _ctx.SystemAlerts
                .AnyAsync(a => a.ProductId == product.Id && !a.IsResolved);

            if (!alertExists)
            {
                var alert = new SystemAlertEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    ProductId = product.Id,
                    SupplierId = product.DefaultSupplierId,
                    Title = $"Stock Crítico: {product.Name}",
                    Message = $"Stock actual ({product.Stock}) por debajo del mínimo permitido ({product.MinStock}).",
                    CreatedAt = DateTime.Now.ToString("s"),
                    IsResolved = false
                };
                _ctx.SystemAlerts.Add(alert);
            }
        }
    }

    private async Task<decimal> DeductInventoryRecursiveAsync(AppDbContext _ctx, string productId, double quantityMultiplier, HashSet<string> executionStack, OperationIdentity actor, string operationId, string documentId, string saleLineId)
    {
        var product = await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.IsActive)
            ?? throw new InvalidOperationException($"Producto o ingrediente no encontrado: {productId}");

        if (product.IsKit)
        {
            if (!executionStack.Add(productId))
                throw new InvalidOperationException($"Error crítico: Referencia circular detectada en el Combo/Kit: {product.Name}");

            var kit = await _ctx.ItemKits
                .Include(k => k.Components)
                .FirstOrDefaultAsync(k => k.ParentProductId == product.Id || k.Id == product.Id)
                ?? throw new InvalidOperationException($"Estructura de Combo no encontrada para: {product.Name}");

            decimal totalKitUnitCost = 0m;

            if (kit.Components.Count == 0 || kit.Components.Any(c => !double.IsFinite(c.Quantity) || c.Quantity <= 0))
                throw new InvalidOperationException("El kit requiere componentes con cantidades válidas.");

            foreach (var component in kit.Components)
            {
                double totalRequired = quantityMultiplier * component.Quantity;
                decimal compUnitCost = await DeductInventoryRecursiveAsync(_ctx, component.ProductId, totalRequired, executionStack, actor, operationId, documentId, saleLineId);
                totalKitUnitCost += Convert.ToDecimal(component.Quantity) * compUnitCost;
            }

            executionStack.Remove(productId);
            return totalKitUnitCost;
        }
        else
        {
            await CommercialLedger.StockAsync(_ctx, actor, operationId, documentId, productId,
                -(decimal)quantityMultiplier, "SALE", saleLineId);
            product.Stock = Math.Round(product.Stock - quantityMultiplier, 3);
            await CheckAndFlagLowStockAsync(_ctx, product);
            return product.Cost;
        }
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
