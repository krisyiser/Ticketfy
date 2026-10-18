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

    private async Task<decimal> DeductInventoryRecursiveAsync(AppDbContext _ctx, string productId, double quantityMultiplier, HashSet<string> executionStack)
    {
        var product = await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId)
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

            foreach (var component in kit.Components)
            {
                double totalRequired = quantityMultiplier * component.Quantity;
                decimal compUnitCost = await DeductInventoryRecursiveAsync(_ctx, component.ProductId, totalRequired, executionStack);
                totalKitUnitCost += Convert.ToDecimal(component.Quantity) * compUnitCost;
            }

            executionStack.Remove(productId);
            return totalKitUnitCost;
        }
        else
        {
            int affected = await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE products
                SET stock = ROUND(stock - {quantityMultiplier}, 3),
                    stock_version = stock_version + 1
                WHERE id = {productId}
                  AND stock_version = {product.StockVersion}
                  AND stock >= {quantityMultiplier};");

            if (affected != 1)
            {
                double? currentStock = await _ctx.Products.AsNoTracking()
                    .Where(p => p.Id == productId)
                    .Select(p => (double?)p.Stock)
                    .FirstOrDefaultAsync();
                if (currentStock.HasValue && currentStock.Value < quantityMultiplier)
                    throw new InvalidOperationException($"Stock insuficiente para '{product.Name}'. Disponible: {currentStock.Value:N3}, requerido: {quantityMultiplier:N3}.");
                throw new DbUpdateConcurrencyException($"El inventario de '{product.Name}' cambió durante el cobro.");
            }

            product.Stock = Math.Round(product.Stock - quantityMultiplier, 3);
            await CheckAndFlagLowStockAsync(_ctx, product);
            return product.Cost;
        }
    }

    private async Task RestockInventoryRecursiveAsync(AppDbContext _ctx, string productId, double quantityMultiplier, HashSet<string> executionStack)
    {
        var product = await _ctx.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId)
            ?? throw new InvalidOperationException($"Producto o ingrediente no encontrado: {productId}");

        if (product.IsKit)
        {
            if (!executionStack.Add(productId))
                throw new InvalidOperationException($"Error crítico: Referencia circular detectada en el Combo/Kit: {product.Name}");

            var kit = await _ctx.ItemKits
                .Include(k => k.Components)
                .FirstOrDefaultAsync(k => k.ParentProductId == product.Id || k.Id == product.Id)
                ?? throw new InvalidOperationException($"Estructura de Combo no encontrada para: {product.Name}");

            foreach (var component in kit.Components)
            {
                double totalRequired = quantityMultiplier * component.Quantity;
                await RestockInventoryRecursiveAsync(_ctx, component.ProductId, totalRequired, executionStack);
            }

            executionStack.Remove(productId);
        }
        else
        {
            int affected = await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE products
                SET stock = ROUND(stock + {quantityMultiplier}, 3),
                    stock_version = stock_version + 1
                WHERE id = {productId}
                  AND stock_version = {product.StockVersion};");
            if (affected != 1)
                throw new DbUpdateConcurrencyException($"El inventario de '{product.Name}' cambió durante la devolución.");
        }
    }

    private List<SaleItemSnapshotDto> ApplyProratedGlobalDiscountAndTaxes(List<SaleItemSnapshotDto> items, decimal globalDiscountAmount)
    {
        decimal totalCartSubtotal = items.Sum(i =>
            (Convert.ToDecimal(i.Quantity) * (i.OriginalUnitPrice > 0m ? i.OriginalUnitPrice : i.UnitPrice))
            - i.AppliedDiscountAmount);
        decimal globalDiscountDec = globalDiscountAmount;
        decimal accumulatedProratedDiscount = 0m;
        var result = new List<SaleItemSnapshotDto>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            decimal itemUnitPrice = item.OriginalUnitPrice > 0m ? item.OriginalUnitPrice : item.UnitPrice;
            decimal lineSubtotal = (Convert.ToDecimal(item.Quantity) * itemUnitPrice) - item.AppliedDiscountAmount;
            decimal proratedGlobalDiscount = 0m;

            if (totalCartSubtotal > 0 && globalDiscountDec > 0)
            {
                if (i == items.Count - 1)
                {
                    proratedGlobalDiscount = globalDiscountDec - accumulatedProratedDiscount;
                }
                else
                {
                    decimal weight = lineSubtotal / totalCartSubtotal;
                    proratedGlobalDiscount = Math.Round(globalDiscountDec * weight, 2);
                    accumulatedProratedDiscount += proratedGlobalDiscount;
                }
            }

            decimal totalLineAmount = Math.Round(lineSubtotal - proratedGlobalDiscount, 2);
            // TAX BREAKDOWN (16% IVA INCLUDED IN RETAIL PRICE)
            decimal taxableBase = Math.Round(totalLineAmount / (1m + (decimal)Ticketfy.Core.Constants.AppConstants.DefaultIvaRate), 2);
            decimal taxAmount = Math.Round(totalLineAmount - taxableBase, 2);

            result.Add(item with
            {
                ProratedGlobalDiscountAmount = proratedGlobalDiscount,
                TaxAmount = taxAmount,
                TotalPrice = totalLineAmount
            });
        }
        return result;
    }
}
