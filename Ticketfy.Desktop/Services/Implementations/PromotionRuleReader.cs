using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal sealed record PromotionTarget(string ProductId, string? Category, double Quantity);

/// <summary>Pricing scans rules in precedence order, retaining at most one winner per cart line, not a catalog page.</summary>
internal static class PromotionRuleReader
{
    internal static IQueryable<PromotionEntity> ActiveQuery(AppDbContext db)
    {
        var zone = BusinessTime.Zone;
        long now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime.Ticks;
        BusinessDateQuery.RegisterOn(db, zone);
        return db.Promotions.FromSqlInterpolated($"""
            SELECT * FROM promotions
            WHERE isActive = 1
                AND ticketfy_business_ticks(start_date) <= {now}
                AND ticketfy_business_ticks(end_date) >= {now}
            """).AsNoTracking().OrderByDescending(p => p.Priority).ThenBy(p => p.Id);
    }

    internal static bool Matches(PromotionEntity p, PromotionTarget target) =>
        ((string.IsNullOrEmpty(p.TargetProductId) && string.IsNullOrEmpty(p.TargetId) && string.IsNullOrEmpty(p.TargetCategory))
            || p.TargetProductId == target.ProductId || p.TargetId == target.ProductId
            || (!string.IsNullOrEmpty(p.TargetCategory) && string.Equals(target.Category, p.TargetCategory, StringComparison.OrdinalIgnoreCase)))
        && target.Quantity >= p.MinQuantity;

    internal static async Task<List<PromotionEntity>> ReadApplicableAsync(AppDbContext db, IReadOnlyList<PromotionTarget> targets)
    {
        var selected = new List<PromotionEntity>();
        if (targets.Count == 0) return selected;
        var resolved = new bool[targets.Count];
        int remaining = targets.Count;
        await foreach (var rule in CandidateQuery(db, targets).AsAsyncEnumerable())
        {
            bool used = false;
            for (int i = 0; i < targets.Count; i++)
            {
                if (resolved[i] || !Matches(rule, targets[i])) continue;
                resolved[i] = true;
                --remaining;
                used = true;
            }
            if (used) selected.Add(rule);
            if (remaining == 0) break;
        }
        return selected;
    }

    private static IQueryable<PromotionEntity> CandidateQuery(AppDbContext db, IReadOnlyList<PromotionTarget> targets)
    {
        Ticketfy.Core.Operations.CommercialDocumentLimits.RequireLineCount(targets.Count);
        if (targets.Any(t => string.IsNullOrWhiteSpace(t.ProductId) || !double.IsFinite(t.Quantity) || t.Quantity <= 0))
            throw new InvalidOperationException("El cálculo de promociones requiere productos y cantidades válidos.");
        string products = JsonSerializer.Serialize(targets.Select(t => t.ProductId).Distinct(StringComparer.Ordinal));
        string categories = JsonSerializer.Serialize(targets.Select(t => t.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase));
        double maximumQuantity = targets.Max(t => t.Quantity);
        var zone = BusinessTime.Zone;
        long now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime.Ticks;
        BusinessDateQuery.RegisterOn(db, zone);
        // Query-only collation keeps the existing Unicode OrdinalIgnoreCase semantics.
        // No index or stored value depends on this connection-local extension.
        ((SqliteConnection)db.Database.GetDbConnection()).CreateCollation("ticketfy_promotion_category",
            (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        // Filter cheap scope/quantity predicates before parsing historical date formats.
        // The exact per-line quantity and precedence checks still happen in Matches.
        return db.Promotions.FromSqlInterpolated($"""
            SELECT * FROM promotions
            WHERE isActive = 1 AND min_quantity <= {maximumQuantity}
                AND (
                    (COALESCE(target_product_id, '') = '' AND COALESCE(targetId, '') = '' AND COALESCE(target_category, '') = '')
                    OR target_product_id COLLATE BINARY IN (SELECT value FROM json_each({products}))
                    OR targetId COLLATE BINARY IN (SELECT value FROM json_each({products}))
                    OR (target_category <> '' AND target_category COLLATE ticketfy_promotion_category IN (SELECT value FROM json_each({categories})))
                )
                AND ticketfy_business_ticks(start_date) <= {now}
                AND ticketfy_business_ticks(end_date) >= {now}
            """).AsNoTracking().OrderByDescending(p => p.Priority).ThenBy(p => p.Id);
    }
}
