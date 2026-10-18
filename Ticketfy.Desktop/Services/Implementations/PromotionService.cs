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
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;

    public PromotionService(AppDbContext context, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(context.Options), operations) { }
    public PromotionService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    public async Task<List<PromotionDto>> GetAllAsync()
    {
        await using var _ctx = await _factory.CreateDbContextAsync();
        var list = await _ctx.Promotions.AsNoTracking().ToListAsync();
        return list.Select(p => new PromotionDto(p.Id, p.Name, p.DiscountValue, p.IsActive == 1, p.StrategyType, p.TargetProductId, p.TargetCategory ?? "", p.MinQuantity, p.FreeQuantity, DateTime.Parse(p.StartDate), DateTime.Parse(p.EndDate), p.Priority)).ToList();
    }

    public async Task<List<PromotionDto>> GetActiveAsync()
    {
        var now = DateTime.Now;
        return (await GetAllAsync()).Where(p => p.IsActive && (!p.StartDate.HasValue || p.StartDate <= now)
            && (!p.EndDate.HasValue || p.EndDate >= now)).OrderByDescending(p => p.Priority).ThenBy(p => p.Id).ToList();
    }

    public async Task SaveAsync(PromotionDto promotion)
    {
        await using var _ctx = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_ctx, Ticketfy.Core.Security.PermissionCatalog.PromotionsManage, false);
        if (!Enum.IsDefined(promotion.StrategyType) || string.IsNullOrWhiteSpace(promotion.Name) || promotion.DiscountValue < 0 || !double.IsFinite(promotion.MinQuantity) || promotion.MinQuantity <= 0
            || !double.IsFinite(promotion.FreeQuantity) || promotion.FreeQuantity < 0
            || (promotion.StrategyType == Ticketfy.Core.Enums.PromotionType.PercentageDiscount && promotion.DiscountValue > 100)
            || (promotion.StartDate.HasValue && promotion.EndDate.HasValue && promotion.StartDate > promotion.EndDate))
            throw new InvalidOperationException("Regla de promoción inválida.");
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
            entity.StartDate = (promotion.StartDate ?? DateTime.Today).ToString("s");
            entity.EndDate = (promotion.EndDate ?? DateTime.Today.AddMonths(1)).ToString("s");
            entity.Priority = promotion.Priority;
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
                StartDate = (promotion.StartDate ?? DateTime.Today).ToString("s"),
                EndDate = (promotion.EndDate ?? DateTime.Today.AddMonths(1)).ToString("s"),
                Priority = promotion.Priority,
                FreeQuantity = promotion.FreeQuantity
            });
        }
        CommercialLedger.Journal(_ctx, actor, Guid.NewGuid().ToString("N"), "PROMOTION_SAVED", promotion.Id, 0, "Regla comercial");
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await using var _ctx = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_ctx, Ticketfy.Core.Security.PermissionCatalog.PromotionsManage, false);
        var entity = await _ctx.Promotions.FindAsync(id);
        if (entity != null)
        {
            entity.IsActive = 0;
            CommercialLedger.Journal(_ctx, actor, Guid.NewGuid().ToString("N"), "PROMOTION_INACTIVATED", id, 0, "Baja lógica");
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems)
    {
        await using var _ctx = await _factory.CreateDbContextAsync();
        if (cartItems == null || cartItems.Count == 0) return cartItems ?? [];

        var nowIso = DateTime.Now.ToString("s");
        var activePromotions = await _ctx.Promotions
            .AsNoTracking()
            .Where(p => p.IsActive == 1 && string.Compare(p.StartDate, nowIso) <= 0 && string.Compare(p.EndDate, nowIso) >= 0)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Id)
            .ToListAsync();

        return Evaluate(cartItems, activePromotions);
    }

    internal static List<CartItemDto> Evaluate(List<CartItemDto> cartItems, List<PromotionEntity> activePromotions)
    {
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
                    decimal stepSize = (decimal)matchingPromo.MinQuantity + (decimal)matchingPromo.FreeQuantity;
                    if (stepSize > 0)
                    {
                        decimal bundleCount = Math.Floor((decimal)item.Quantity / stepSize);
                        decimal totalFreeUnits = bundleCount * (decimal)matchingPromo.FreeQuantity;
                        item.AppliedDiscountAmount = Math.Round(totalFreeUnits * item.OriginalUnitPrice, 2);
                    }
                    break;
            }
            item.AppliedDiscountAmount = Math.Min(item.AppliedDiscountAmount, item.OriginalUnitPrice * (decimal)item.Quantity);
        }

        return cartItems;
    }
}
