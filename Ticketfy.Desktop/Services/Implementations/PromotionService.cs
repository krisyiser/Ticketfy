using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

public class PromotionService : IPromotionService
{
    private readonly AppDbContext _ctx;

    public PromotionService(AppDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<List<PromotionDto>> GetAllAsync()
    {
        var list = await _ctx.Promotions.AsNoTracking().ToListAsync();
        return list.Select(p => new PromotionDto(p.Id, p.Name, p.DiscountValue, p.IsActive == 1, p.StrategyType, p.TargetProductId, p.TargetCategory ?? "", p.MinQuantity, p.FreeQuantity)).ToList();
    }

    public async Task<List<PromotionDto>> GetActiveAsync()
    {
        var list = await _ctx.Promotions.AsNoTracking().Where(p => p.IsActive == 1).ToListAsync();
        return list.Select(p => new PromotionDto(p.Id, p.Name, p.DiscountValue, p.IsActive == 1, p.StrategyType, p.TargetProductId, p.TargetCategory ?? "", p.MinQuantity, p.FreeQuantity)).ToList();
    }

    public async Task SaveAsync(PromotionDto promotion)
    {
        var entity = await _ctx.Promotions.FindAsync(promotion.Id);
        if (entity != null)
        {
            entity.Name = promotion.Name;
            entity.DiscountValue = promotion.DiscountValue;
            entity.IsActive = promotion.IsActive ? 1 : 0;
            entity.StrategyType = promotion.StrategyType;
            entity.TargetProductId = promotion.TargetProductId;
            entity.TargetCategory = promotion.TargetCategory ?? "";
            entity.MinQuantity = promotion.MinQuantity;
            entity.FreeQuantity = promotion.FreeQuantity;
        }
        else
        {
            _ctx.Promotions.Add(new PromotionEntity
            {
                Id = string.IsNullOrEmpty(promotion.Id) ? Guid.NewGuid().ToString() : promotion.Id,
                Name = promotion.Name,
                DiscountValue = promotion.DiscountValue,
                IsActive = promotion.IsActive ? 1 : 0,
                StrategyType = promotion.StrategyType,
                TargetProductId = promotion.TargetProductId,
                TargetCategory = promotion.TargetCategory ?? "",
                MinQuantity = promotion.MinQuantity,
                FreeQuantity = promotion.FreeQuantity
            });
        }
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _ctx.Promotions.FindAsync(id);
        if (entity != null)
        {
            _ctx.Promotions.Remove(entity);
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems)
    {
        if (cartItems == null || cartItems.Count == 0) return cartItems ?? [];

        var nowIso = DateTime.Now.ToString("s");
        var activePromotions = await _ctx.Promotions
            .AsNoTracking()
            .Where(p => p.IsActive == 1 && string.Compare(p.StartDate, nowIso) <= 0 && string.Compare(p.EndDate, nowIso) >= 0)
            .OrderByDescending(p => p.Priority)
            .ToListAsync();

        if (activePromotions.Count == 0)
        {
            activePromotions = await _ctx.Promotions
                .AsNoTracking()
                .Where(p => p.IsActive == 1)
                .OrderByDescending(p => p.Priority)
                .ToListAsync();
        }

        foreach (var item in cartItems)
        {
            if (item.OriginalUnitPrice <= 0)
            {
                item.OriginalUnitPrice = item.UnitPrice;
            }

            // Reset discounts before re-evaluating
            item.AppliedDiscountAmount = 0m;
            item.AppliedPromotionId = null;
            item.PromotionDescription = string.Empty;

            var matchingPromo = activePromotions.FirstOrDefault(p =>
                ((string.IsNullOrEmpty(p.TargetProductId) && string.IsNullOrEmpty(p.TargetId) && string.IsNullOrEmpty(p.TargetCategory)) ||
                 p.TargetProductId == item.ProductId || p.TargetId == item.ProductId ||
                 (!string.IsNullOrEmpty(p.TargetCategory) && item.Category.Equals(p.TargetCategory, StringComparison.OrdinalIgnoreCase))) &&
                item.Quantity >= p.MinQuantity);

            if (matchingPromo == null) continue;

            item.AppliedPromotionId = matchingPromo.Id;
            item.PromotionDescription = matchingPromo.Name;

            switch (matchingPromo.StrategyType)
            {
                case Ticketfy.Core.Enums.PromotionType.PercentageDiscount:
                    item.AppliedDiscountAmount = Math.Round(item.OriginalUnitPrice * (matchingPromo.DiscountValue / 100m) * (decimal)item.Quantity, 2);
                    break;

                case Ticketfy.Core.Enums.PromotionType.FixedAmountDiscount:
                    item.AppliedDiscountAmount = Math.Round(matchingPromo.DiscountValue * (decimal)item.Quantity, 2);
                    break;

                case Ticketfy.Core.Enums.PromotionType.WholesalePrice:
                case Ticketfy.Core.Enums.PromotionType.VolumeTier:
                    decimal unitSavings = Math.Max(0m, item.OriginalUnitPrice - matchingPromo.DiscountValue);
                    item.AppliedDiscountAmount = Math.Round(unitSavings * (decimal)item.Quantity, 2);
                    break;

                case Ticketfy.Core.Enums.PromotionType.BuyNGetM:
                    int stepSize = (int)(matchingPromo.MinQuantity + matchingPromo.FreeQuantity);
                    if (stepSize > 0)
                    {
                        int bundleCount = (int)item.Quantity / stepSize;
                        decimal totalFreeUnits = bundleCount * (decimal)matchingPromo.FreeQuantity;
                        item.AppliedDiscountAmount = Math.Round(totalFreeUnits * item.OriginalUnitPrice, 2);
                    }
                    break;
            }
        }

        return cartItems;
    }
}
