using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Called only inside an authorized read snapshot. Scalar totals cover the entire cut, not just the page.</summary>
internal static class CommercialLedgerReader
{
    internal const int LegacyLimit = 200;
    private static int Offset(int page, int size)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        return checked(page * size);
    }

    private static LedgerReadCut ValidateCut(LedgerReadCut? cut, string ledger, string owner, long latest, int page)
    {
        if (cut is null)
        {
            if (page != 0) throw new InvalidOperationException("Inicie la consulta antes de cambiar de página.");
            return new(ledger, owner, latest, DateTimeOffset.UtcNow.ToString("O"));
        }
        if (cut.Ledger != ledger || cut.OwnerId != owner || cut.ThroughSequence < 0 || cut.ThroughSequence > latest
            || !DateTimeOffset.TryParse(cut.CapturedAtUtc, out _))
            throw new InvalidOperationException("La consulta ya no corresponde a este registro. Actualice el historial.");
        return cut;
    }

    internal static async Task<AccountStatementPageDto> AccountAsync(AppDbContext db, AccountKind kind, string id,
        int page, int size, LedgerReadCut? cut)
    {
        int offset = Offset(page, size);
        var query = db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == kind && e.AccountId == id);
        long latest = await query.MaxAsync(e => (long?)e.PostingSequence) ?? 0;
        cut = ValidateCut(cut, kind.ToString(), id, latest, page);
        var visible = query.Where(e => e.PostingSequence <= cut.ThroughSequence);
        var entries = await visible.OrderByDescending(e => e.PostingSequence).Skip(offset).Take(size + 1).ToListAsync();
        bool more = entries.Count > size;
        entries = entries.Take(size).Reverse().ToList();
        long first = entries.FirstOrDefault()?.PostingSequence ?? 0;
        decimal balance = 0m, opening = 0m, currentBalance = 0m;
        long count = 0;
        bool migration = false;
        await foreach (var row in query.Select(e => new { e.PostingSequence, e.Amount, Opening = e.EntryType == "MIGRATION_OPENING" }).AsAsyncEnumerable())
        {
            currentBalance += row.Amount;
            if (row.PostingSequence > cut.ThroughSequence) continue;
            balance += row.Amount;
            if (row.PostingSequence < first) opening += row.Amount;
            migration |= row.Opening;
            count++;
        }
        if (page > 0 && entries.Count == 0) throw new InvalidOperationException("La página no contiene registros en esta consulta.");
        decimal? cached = null;
        bool unverified;
        if (kind == AccountKind.Customer)
        {
            cached = await db.Customers.Where(c => c.Id == id).Select(c => (decimal?)c.Debt).SingleOrDefaultAsync()
                ?? throw new InvalidOperationException("Cliente inexistente.");
            unverified = cached != currentBalance || migration && !await db.HistoricalBalanceReconciliations.AnyAsync(
                r => r.Scope == HistoricalBalanceScope.CustomerAccount && r.TargetId == id);
        }
        else if (kind == AccountKind.Supplier)
        {
            if (!await db.Suppliers.AnyAsync(s => s.Id == id)) throw new InvalidOperationException("Proveedor inexistente.");
            unverified = await db.Purchases.AnyAsync(p => p.SupplierId == id && p.LedgerVersion == 0
                && !db.HistoricalBalanceReconciliations.Any(r => r.Scope == HistoricalBalanceScope.SupplierPurchase && r.TargetId == p.Id));
        }
        else if (kind == AccountKind.Giftcard)
        {
            cached = await db.Giftcards.Where(g => g.CardNumber == id).Select(g => (decimal?)g.Balance).SingleOrDefaultAsync()
                ?? throw new InvalidOperationException("Monedero inexistente.");
            unverified = cached != currentBalance || migration;
        }
        else throw new ArgumentOutOfRangeException(nameof(kind));
        return new(id, balance, opening, entries, cut, count, more, latest > cut.ThroughSequence, unverified, migration, cached);
    }

    internal static async Task<InventoryLedgerPageDto> InventoryAsync(AppDbContext db, string id, int page, int size,
        LedgerReadCut? cut, bool costsVisible)
    {
        int offset = Offset(page, size);
        double stock = await db.Products.Where(p => p.Id == id).Select(p => (double?)p.Stock).SingleOrDefaultAsync()
            ?? throw new InvalidOperationException("Producto inexistente.");
        var query = db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == id);
        long latest = await query.MaxAsync(m => (long?)m.PostingSequence) ?? 0;
        cut = ValidateCut(cut, "Inventory", id, latest, page);
        var visible = query.Where(m => m.PostingSequence <= cut.ThroughSequence);
        long count = await visible.LongCountAsync();
        var entries = await visible.OrderByDescending(m => m.PostingSequence).Skip(offset).Take(size + 1).ToListAsync();
        bool more = entries.Count > size;
        entries = entries.Take(size).Reverse().ToList();
        if (page > 0 && entries.Count == 0) throw new InvalidOperationException("La página no contiene registros en esta consulta.");
        if (!costsVisible) foreach (var entry in entries) entry.UnitCost = 0m;
        decimal? ledgerStock = await visible.OrderByDescending(m => m.PostingSequence).Select(m => (decimal?)m.StockAfter).FirstOrDefaultAsync();
        decimal? currentLedgerStock = latest == cut.ThroughSequence ? ledgerStock :
            await query.OrderByDescending(m => m.PostingSequence).Select(m => (decimal?)m.StockAfter).FirstOrDefaultAsync();
        bool unverified = currentLedgerStock is null ? stock != 0 : currentLedgerStock != (decimal)stock;
        bool migration = await visible.AnyAsync(m => m.Reason == "MIGRATION_OPENING");
        return new(id, entries, cut, count, more, latest > cut.ThroughSequence, ledgerStock, (decimal)stock, unverified, migration, costsVisible);
    }

    internal static void RequireComplete(bool more)
    {
        if (more) throw new InvalidOperationException("El historial supera 200 registros. Utilice la consulta paginada; no se entregará un estado parcial como si fuera completo.");
    }
}
