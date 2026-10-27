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
        var page = await GetPageAsync(0, LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(page.HasMore);
        return page.Items;
    }

    public async Task<CatalogPage<PromotionDto>> GetPageAsync(int page, int size = 100, string? search = null)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.PromotionsManage, false);
        var query = db.Promotions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            string value = search.Trim().ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(value) || p.Id.ToLower().Contains(value)
                || p.TargetCategory.ToLower().Contains(value));
        }
        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(offset).Take(size + 1).ToListAsync();
        return new(rows.Take(size).Select(Map).ToList(), rows.Count > size);
    }

    public async Task<List<PromotionDto>> GetActiveAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.PosCheckout, false);
        var rows = await PromotionRuleReader.ActiveQuery(db).Take(LegacyCatalogReadPolicy.Limit + 1).ToListAsync();
        LegacyCatalogReadPolicy.RequireComplete(rows.Count > LegacyCatalogReadPolicy.Limit);
        return rows.Select(Map).ToList();
    }

    private static PromotionDto Map(PromotionEntity p)
    {
        DateTime? start = Ticketfy.Core.Helpers.BusinessTime.TryLocal(p.StartDate, out var from) ? from : null;
        DateTime? end = Ticketfy.Core.Helpers.BusinessTime.TryLocal(p.EndDate, out var to) ? to : null;
        return new(p.Id, p.Name, p.DiscountValue, p.IsActive == 1, p.StrategyType,
            string.IsNullOrEmpty(p.TargetProductId) ? p.TargetId : p.TargetProductId, p.TargetCategory ?? "",
            p.MinQuantity, p.FreeQuantity, start, end, p.Priority,
            start is null || end is null || start > end ? "Vigencia sin verificar: revise las fechas antes de activar o editar." : null, p.Revision);
    }

    private static void Validate(PromotionDto promotion)
    {
        if (promotion.ReadWarning is not null && (!promotion.StartDate.HasValue || !promotion.EndDate.HasValue))
            throw new InvalidOperationException("Revise explícitamente la vigencia heredada antes de guardar.");
        if (!Enum.IsDefined(promotion.StrategyType) || string.IsNullOrWhiteSpace(promotion.Name) || promotion.DiscountValue < 0 || !double.IsFinite(promotion.MinQuantity) || promotion.MinQuantity <= 0
            || !double.IsFinite(promotion.FreeQuantity) || promotion.FreeQuantity < 0
            || (promotion.StrategyType == Ticketfy.Core.Enums.PromotionType.PercentageDiscount && promotion.DiscountValue > 100)
            || (promotion.StartDate.HasValue && promotion.EndDate.HasValue && promotion.StartDate > promotion.EndDate))
            throw new InvalidOperationException("Regla de promoción inválida.");
    }

    public async Task SaveAsync(PromotionDto promotion, string operationId)
    {
        operationId = CommercialLedger.OperationId(operationId);
        await using var _ctx = await _factory.CreateDbContextAsync();
        await using var tx = await _ctx.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_ctx, Ticketfy.Core.Security.PermissionCatalog.PromotionsManage, false);
        bool replay = await _ctx.CommandReceipts.AnyAsync(c => c.OperationId == operationId);
        await CommandIdentity.BindAsync(_ctx, actor, operationId, "PROMOTION_SAVE", promotion.Id?.Trim(),
            promotion.Revision, promotion.Name, promotion.DiscountValue, promotion.IsActive, promotion.StrategyType,
            promotion.TargetProductId, promotion.TargetCategory, promotion.MinQuantity, promotion.FreeQuantity,
            promotion.StartDate?.Ticks, promotion.EndDate?.Ticks, promotion.Priority);
        if (replay) return; // Never apply the old draft again, even if another command changed the rule.
        Validate(promotion);
        var today = Ticketfy.Core.Helpers.BusinessTime.Today;
        promotion = promotion with { StartDate = promotion.StartDate ?? today, EndDate = promotion.EndDate ?? today.AddMonths(1) };
        Validate(promotion);
        string id = string.IsNullOrWhiteSpace(promotion.Id) ? Guid.NewGuid().ToString("N") : promotion.Id.Trim();
        var entity = await _ctx.Promotions.FindAsync(id);
        if (entity is null ? promotion.Revision.HasValue : promotion.Revision != entity.Revision)
            throw new PromotionConflictException();
        if (entity != null)
        {
            AdvanceRevision(entity);
            entity.Name = promotion.Name;
            entity.DiscountValue = promotion.DiscountValue;
            entity.IsActive = promotion.IsActive ? 1 : 0;
            entity.StrategyType = promotion.StrategyType;
            entity.TargetProductId = promotion.TargetProductId;
            entity.TargetId = null; // An explicit edit replaces the legacy target, not an additional OR target.
            entity.TargetCategory = promotion.TargetCategory ?? "";
            entity.MinQuantity = promotion.MinQuantity;
            entity.FreeQuantity = promotion.FreeQuantity;
            entity.StartDate = PreserveSchedule(entity.StartDate, promotion.StartDate!.Value);
            entity.EndDate = PreserveSchedule(entity.EndDate, promotion.EndDate!.Value);
            entity.Priority = promotion.Priority;
        }
        else
        {
            _ctx.Promotions.Add(new PromotionEntity
            {
                Id = id,
                Name = promotion.Name,
                DiscountValue = promotion.DiscountValue,
                IsActive = promotion.IsActive ? 1 : 0,
                StrategyType = promotion.StrategyType,
                TargetProductId = promotion.TargetProductId,
                TargetCategory = promotion.TargetCategory ?? "",
                MinQuantity = promotion.MinQuantity,
                StartDate = FormatSchedule(promotion.StartDate!.Value),
                EndDate = FormatSchedule(promotion.EndDate!.Value),
                Priority = promotion.Priority,
                FreeQuantity = promotion.FreeQuantity
            });
        }
        CommercialLedger.Journal(_ctx, actor, operationId, "PROMOTION_SAVED", id, 0,
            $"Regla comercial; revisión {entity?.Revision ?? 0}.");
        await _ctx.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static string FormatSchedule(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static void AdvanceRevision(PromotionEntity entity)
    {
        if (entity.Revision < 0 || entity.Revision == long.MaxValue)
            throw new InvalidOperationException("La revisión de la promoción requiere revisión técnica; no se guardó el cambio.");
        entity.Revision++;
    }

    private static string PreserveSchedule(string original, DateTime value) =>
        Ticketfy.Core.Helpers.BusinessTime.TryLocal(original, out var previous) && previous == value
            ? original : FormatSchedule(value);

    public Task DeleteAsync(string id, string operationId) => SetActiveAsync(id, false, operationId);

    public async Task SetActiveAsync(string id, bool active, string operationId)
    {
        operationId = CommercialLedger.OperationId(operationId);
        await using var _ctx = await _factory.CreateDbContextAsync();
        await using var tx = await _ctx.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_ctx, Ticketfy.Core.Security.PermissionCatalog.PromotionsManage, false);
        bool replay = await _ctx.CommandReceipts.AnyAsync(c => c.OperationId == operationId);
        await CommandIdentity.BindAsync(_ctx, actor, operationId, "PROMOTION_STATUS", id, active);
        if (replay) return;
        var entity = await _ctx.Promotions.FindAsync(id) ?? throw new InvalidOperationException("La promoción ya no existe; actualice el directorio.");
        if (active) Validate(Map(entity));
        int state = active ? 1 : 0;
        if (entity.IsActive == state)
        {
            // A confirmed no-op also needs a receipt: retrying it later must not undo another operator.
            await _ctx.SaveChangesAsync(); await tx.CommitAsync();
            return;
        }
        AdvanceRevision(entity);
        entity.IsActive = state;
        CommercialLedger.Journal(_ctx, actor, operationId,
            active ? "PROMOTION_ACTIVATED" : "PROMOTION_INACTIVATED", id, 0,
            $"Cambio de habilitación; conserva la regla. Revisión {entity.Revision}.");
        await _ctx.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems)
    {
        await using var _ctx = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(_ctx);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(_ctx, Ticketfy.Core.Security.PermissionCatalog.PosCheckout, false);
        if (cartItems == null || cartItems.Count == 0) return cartItems ?? [];

        var activePromotions = await PromotionRuleReader.ReadApplicableAsync(_ctx,
            cartItems.Select(i => new PromotionTarget(i.ProductId, i.Category, i.Quantity)).ToList());

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

            var matchingPromo = activePromotions.FirstOrDefault(p => PromotionRuleReader.Matches(p,
                new PromotionTarget(item.ProductId, item.Category, item.Quantity)));

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
