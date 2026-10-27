using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Implementations;

public sealed partial class GiftcardService
{
    public async Task<WalletDirectoryPage> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.WalletManage, false);
        var query = db.Giftcards.AsNoTracking().Where(g => includeInactive || g.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string text = search.Trim().ToLowerInvariant();
            query = query.Where(g => g.CardNumber.ToLower().Contains(text) || g.Id.ToLower().Contains(text)
                || g.CustomerId != null && g.CustomerId.ToLower().Contains(text));
        }
        // Stream only monetary scalars; never load the directory to calculate its complete liability.
        // Decimal avoids a floating-point sum and SQLite integer aggregate overflow for large histories.
        decimal total = 0m;
        long count = 0;
        await foreach (decimal amount in query.Select(g => g.Balance).AsAsyncEnumerable())
        { total = checked(total + amount); count++; }
        var rows = await query.OrderBy(g => g.CardNumber).ThenBy(g => g.Id).Skip(offset).Take(size + 1)
            .Select(g => new WalletDirectoryRow(g.Id, g.CardNumber, g.Balance, g.CustomerId, g.IsActive)).ToListAsync();
        return new(rows.Take(size).ToList(), rows.Count > size, count, total);
    }
}
