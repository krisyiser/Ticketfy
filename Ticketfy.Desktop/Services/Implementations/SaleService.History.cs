using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Implementations;

public partial class SaleService
{
    private sealed class SaleIssuance
    {
        public string Date { get; set; } = "";
        public decimal Total { get; set; }
    }
    public async Task<SalesHistoryPage> GetHistoryPageAsync(DateTime start, DateTime end, int page, int size = 100)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        var period = new BusinessDateQuery(start, end);
        start = period.Start; end = period.End;
        var zone = period.Zone;
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.ReportsView, false);

        // Read just the issuance scalars, not items JSON or tender/account ledgers for
        // every ticket. Decimal arithmetic and the business calendar stay in managed code.
        int count = 0, unparseable = 0;
        var counts = new int[24];
        var amounts = new decimal[24];
        await foreach (var sale in db.Database.SqlQuery<SaleIssuance>(System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $"SELECT {SaleDocumentReader.SafeDate} AS Date, CAST(total AS TEXT) AS Total FROM sales WHERE date NOT LIKE '____-__-__%' OR (date >= {{0}} AND date < {{1}})",
            period.Lower, period.Upper)).AsAsyncEnumerable())
        {
            if (!BusinessTime.TryLocal(sale.Date, out var local, zone)) { unparseable++; continue; }
            if (local < start || local > end) continue;
            count = checked(count + 1);
            counts[local.Hour] = checked(counts[local.Hour] + 1);
            amounts[local.Hour] += sale.Total / 100m;
        }
        // Legacy dates cannot be ordered lexicographically with ISO/offset timestamps.
        // A connection-local, deterministic function uses the exact same frozen zone
        // as the summary. SQL orders and pages before hydrating any ticket details.
        period.RegisterOn(db);
        var rows = await SaleDocumentReader.HeadersAsync(db, System.Runtime.CompilerServices.FormattableStringFactory.Create($$"""
            SELECT rowid FROM sales
            WHERE (date NOT LIKE '____-__-__%' OR (date >= {0} AND date < {1}))
              AND ticketfy_business_ticks({{SaleDocumentReader.SafeDate}}) >= {2}
              AND ticketfy_business_ticks({{SaleDocumentReader.SafeDate}}) <= {3}
            ORDER BY ticketfy_business_ticks({{SaleDocumentReader.SafeDate}}) DESC, id COLLATE BINARY
            LIMIT {4} OFFSET {5}
            """, period.Lower, period.Upper, start.Ticks, end.Ticks, size, offset));
        var hours = Enumerable.Range(0, 24).Where(hour => counts[hour] > 0)
            .Select(hour => new HourlySaleTotal(hour, counts[hour], amounts[hour])).ToList();
        var items = await MapManyWithLedgersAsync(db, rows);
        if (await _operations.RequireAsync(db, PermissionCatalog.ReportsView, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return new(items, count, (long)offset + rows.Count < count, hours, unparseable);
    }
}
