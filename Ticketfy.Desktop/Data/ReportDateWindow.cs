using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Ticketfy.Data;

/// <summary>SQL prefilter for mixed legacy wall-clock and UTC text dates; exact business-zone check follows.</summary>
internal static class ReportDateWindow
{
    public static IQueryable<T> InCandidateDateRange<T>(this IQueryable<T> query, Expression<Func<T, string?>> date,
        DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
            throw new InvalidOperationException("La fecha inicial no puede ser posterior a la final.");
        if (!start.HasValue && !end.HasValue) return query;
        var value = date.Body;
        var compare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
        Expression? range = null;
        // Two days cover all supported UTC offsets, historical DST and day-inclusive end bounds.
        if (start.HasValue && start.Value > DateTime.MinValue.AddDays(2))
            range = Expression.GreaterThanOrEqual(Expression.Call(compare, value,
                Expression.Constant(start.Value.Date.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), Expression.Constant(0));
        if (end.HasValue && end.Value < DateTime.MaxValue.AddDays(-3))
        {
            var upper = Expression.LessThan(Expression.Call(compare, value,
                Expression.Constant(end.Value.Date.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), Expression.Constant(0));
            range = range is null ? upper : Expression.AndAlso(range, upper);
        }
        if (range is null) return query;
        // Non-ISO legacy strings must not disappear from reports; parse them in the exact filter.
        var like = Expression.Call(typeof(DbFunctionsExtensions), nameof(DbFunctionsExtensions.Like), [],
            Expression.Property(null, typeof(EF), nameof(EF.Functions)), value, Expression.Constant("____-__-__%"));
        return query.Where(Expression.Lambda<Func<T, bool>>(Expression.OrElse(Expression.Not(like), range), date.Parameters));
    }
}
