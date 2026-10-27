using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed record AccountDocumentPages(List<AccountDocumentBalance> Sources, List<AccountDocumentBalance> Targets,
    bool HasMoreSources, bool HasMoreTargets, decimal AvailableCredit, decimal Debt);

public sealed partial class AccountCreditSettlementService
{
    private sealed class DocumentBalanceRow
    {
        public string DocumentId { get; set; } = "";
        public decimal Balance { get; set; }
        public decimal Reserved { get; set; }
        public string Date { get; set; } = "";
    }

    public async Task<AccountDocumentPages> GetDocumentPagesAsync(AccountKind kind, string account, int sourcePage = 0, int targetPage = 0, int size = 100)
    {
        ValidateAccountKind(kind); PurchaseDocumentReader.RequireKey(account, nameof(account));
        if (sourcePage < 0 || targetPage < 0 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(size));
        int sourceOffset = checked(sourcePage * size), targetOffset = checked(targetPage * size);
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        string permission = kind == AccountKind.Customer ? PermissionCatalog.CustomersView : PermissionCatalog.PurchasesManage;
        var actor = await operations.RequireAsync(db, permission, false);
        var entries = db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == kind && e.AccountId == account);
        var requests = PendingRequests(db).Where(r => r.AccountKind == kind && r.AccountId == account);
        string nul = "\0"; // Parameterize: SQLite cannot parse an embedded NUL in a SQL literal.
        if (await entries.AnyAsync(e => e.DocumentId.Trim() == "" || e.DocumentId.Length > 128 || e.DocumentId.Contains(nul) ||
                e.CreatedAtUtc.Length > 128 || e.CreatedAtUtc.Contains(nul)) ||
            await requests.AnyAsync(r => r.Amount <= 0 || !entries.Any(e => e.DocumentId == r.SourceDocumentId)))
            throw new InvalidOperationException("Los documentos o reservas de esta cuenta requieren conciliación. No se muestran saldos parciales.");
        var posted = entries.GroupBy(e => e.DocumentId).Select(g => new { Id = g.Key, Amount = g.Sum(e => e.Amount), Date = g.Min(e => e.CreatedAtUtc) });
        var reserved = requests.GroupBy(r => r.SourceDocumentId).Select(g => new { Id = g.Key, Amount = g.Sum(r => r.Amount) });
        var query = from p in posted
                    join r in reserved on p.Id equals r.Id into matches
                    from r in matches.DefaultIfEmpty()
                    select new DocumentBalanceRow { DocumentId = p.Id, Balance = p.Amount + ((decimal?)r.Amount ?? 0m), Reserved = (decimal?)r.Amount ?? 0m, Date = p.Date! };
        query = query.TagWith("Account credit document totals");
        if (await query.AnyAsync(d => d.Reserved > 0 && d.Balance > 0))
            throw new InvalidOperationException("Las reservas exceden el saldo a favor del documento. Concilie la cuenta antes de operar.");
        decimal credit = -await query.Where(d => d.Balance < 0).SumAsync(d => d.Balance);
        decimal debt = await query.Where(d => d.Balance > 0).SumAsync(d => d.Balance);
        var sources = await query.Where(d => d.Balance < 0).OrderBy(d => d.Date).ThenBy(d => d.DocumentId).Skip(sourceOffset).Take(size + 1).ToListAsync();
        var targets = await query.Where(d => d.Balance > 0).OrderBy(d => d.Date).ThenBy(d => d.DocumentId).Skip(targetOffset).Take(size + 1).ToListAsync();
        if (await operations.RequireAsync(db, permission, false) != actor) throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar los documentos.");
        return new(sources.Take(size).Select(d => new AccountDocumentBalance(d.DocumentId, d.Balance, d.Date)).ToList(),
            targets.Take(size).Select(d => new AccountDocumentBalance(d.DocumentId, d.Balance, d.Date)).ToList(), sources.Count > size, targets.Count > size, credit, debt);
    }
}
