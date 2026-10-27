using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Explicit current-balance adjustments, never reconstruction of missing payments, stock or original attribution.</summary>
public sealed class HistoricalReconciliationService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public async Task<List<HistoricalBalanceTargetDto>> GetTargetsAsync(HistoricalBalanceScope scope, string? search = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        await operations.RequireAsync(db, PermissionCatalog.HistoryReconcile, false);
        string term = search?.Trim().ToLowerInvariant() ?? "";
        // Inactive accounts still require reconciliation; catalog filters must not hide their balances.
        return scope switch
        {
            HistoricalBalanceScope.CustomerAccount => await db.Customers.AsNoTracking()
                .Where(c => term == "" || c.Name.ToLower().Contains(term) || c.CustomerCode.ToLower().Contains(term) || c.Id.ToLower().Contains(term))
                .OrderBy(c => c.Name).ThenBy(c => c.Id).Take(100)
                .Select(c => new HistoricalBalanceTargetDto(c.Id, c.Name + (c.IsActive ? "" : " (inactivo)") + " · " + c.Id)).ToListAsync(),
            HistoricalBalanceScope.SupplierPurchase => await db.Purchases.AsNoTracking().Where(p => p.LedgerVersion == 0
                && (term == "" || p.SupplierName.ToLower().Contains(term) || p.InvoiceNumber.ToLower().Contains(term) || p.Id.ToLower().Contains(term)))
                .OrderBy(p => p.SupplierName).ThenBy(p => p.Date).ThenBy(p => p.Id).Take(100)
                .Select(p => new HistoricalBalanceTargetDto(p.Id, p.SupplierName + " · " + p.InvoiceNumber + " · " + p.Id)).ToListAsync(),
            _ => throw new InvalidOperationException("Tipo de conciliación desconocido.")
        };
    }
    public async Task<HistoricalBalancePreview> PreviewAsync(HistoricalBalanceScope scope, string targetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await operations.RequireAsync(db, PermissionCatalog.HistoryReconcile, false);
        return await ReadAsync(db, scope, targetId);
    }
    private static async Task<HistoricalBalancePreview> ReadAsync(AppDbContext db, HistoricalBalanceScope scope, string targetId)
    {
        string account; string label; decimal? cached = null; decimal? maximum = null; string source;
        if (scope == HistoricalBalanceScope.CustomerAccount)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == targetId)
                ?? throw new InvalidOperationException("Cliente inexistente.");
            account = customer.Id; label = customer.Name; cached = customer.Debt;
            source = CommandIdentity.Hash(customer.Id, customer.Name, customer.Debt, customer.IsActive);
        }
        else if (scope == HistoricalBalanceScope.SupplierPurchase)
        {
            var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(p => p.Id == targetId && p.LedgerVersion == 0)
                ?? throw new InvalidOperationException("Seleccione una compra histórica; las compras nuevas ya tienen su propio ledger.");
            account = purchase.SupplierId; label = purchase.SupplierName + " · " + purchase.InvoiceNumber; maximum = purchase.TotalCost;
            source = CommandIdentity.Hash(purchase.Id, purchase.SupplierId, purchase.InvoiceNumber, purchase.Date, purchase.TotalCost, purchase.Notes);
        }
        else throw new InvalidOperationException("Tipo de conciliación desconocido.");
        var kind = scope == HistoricalBalanceScope.CustomerAccount ? AccountKind.Customer : AccountKind.Supplier;
        var entries = await db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == kind && e.AccountId == account
            && (scope == HistoricalBalanceScope.CustomerAccount || e.DocumentId == targetId)).OrderBy(e => e.Id).ToListAsync();
        var revisions = await db.HistoricalBalanceReconciliations.AsNoTracking().Where(r => r.Scope == scope && r.TargetId == targetId)
            .OrderBy(r => r.OperationId).Select(r => r.OperationId).ToListAsync();
        string fingerprint = CommandIdentity.Hash(scope, source, string.Join("", entries.Select(e => CommandIdentity.Hash(e.Id, e.DocumentId, e.Amount))),
            string.Join("|", revisions));
        return new(scope, targetId, account, label, entries.Sum(e => e.Amount), cached, maximum, fingerprint);
    }
    public async Task<HistoricalBalanceReconciliationEntity> ConfirmAsync(HistoricalBalanceScope scope, string targetId,
        decimal confirmedBalance, string fingerprint, string evidence, string reason, bool verified, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (!verified || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 200 || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
            || CommercialLedger.Money(confirmedBalance) != confirmedBalance)
            throw new InvalidOperationException("Confirme el saldo con evidencia, motivo e importe a dos decimales.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.HistoryReconcile, false);
        await CommandIdentity.BindAsync(db, actor, operationId, "HISTORICAL_BALANCE", scope, targetId, confirmedBalance, fingerprint, evidence.Trim(), reason.Trim());
        var previous = await db.HistoricalBalanceReconciliations.FindAsync(operationId);
        if (previous is not null) return previous;
        var preview = await ReadAsync(db, scope, targetId);
        if (preview.Fingerprint != fingerprint) throw new InvalidOperationException("El saldo o documento cambió. Consulte una nueva vista previa antes de conciliar.");
        if (scope == HistoricalBalanceScope.SupplierPurchase && (confirmedBalance < 0 || confirmedBalance > preview.MaximumBalance))
            throw new InvalidOperationException("El saldo de la compra debe estar entre cero y su importe original. Un crédito adicional requiere su propio documento.");
        decimal adjustment = confirmedBalance - preview.LedgerBalance;
        var record = new HistoricalBalanceReconciliationEntity
        {
            OperationId = operationId,
            Scope = scope,
            TargetId = targetId,
            AccountId = preview.AccountId,
            LedgerBefore = preview.LedgerBalance,
            CachedBefore = preview.CachedBalance,
            ConfirmedBalance = confirmedBalance,
            Adjustment = adjustment,
            SourceFingerprint = fingerprint,
            EvidenceReference = evidence.Trim(),
            Reason = reason.Trim(),
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId
        };
        db.HistoricalBalanceReconciliations.Add(record);
        string document = scope == HistoricalBalanceScope.CustomerAccount ? "OPENING:" + targetId : targetId;
        if (adjustment != 0)
            CommercialLedger.Account(db, actor, operationId, scope == HistoricalBalanceScope.CustomerAccount ? AccountKind.Customer : AccountKind.Supplier,
                preview.AccountId, document, adjustment, "HISTORICAL_BALANCE_ADJUSTMENT");
        if (scope == HistoricalBalanceScope.CustomerAccount)
        {
            long before = Convert.ToInt64(preview.CachedBalance!.Value * 100m), after = Convert.ToInt64(confirmedBalance * 100m);
            if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET debt = {after} WHERE id = {targetId} AND debt = {before}") != 1)
                throw new InvalidOperationException("El saldo del cliente cambió durante la conciliación.");
        }
        CommercialLedger.Journal(db, actor, operationId, "HISTORICAL_BALANCE_RECONCILED", targetId, adjustment, "Ajuste documentado, sin movimiento de efectivo o inventario");
        await db.SaveChangesAsync(); await tx.CommitAsync(); return record;
    }
    public async Task<List<HistoricalBalanceReconciliationEntity>> GetHistoryAsync(HistoricalBalanceScope scope, string targetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await operations.RequireAsync(db, PermissionCatalog.HistoryReconcile, false);
        return await db.HistoricalBalanceReconciliations.AsNoTracking().Where(r => r.Scope == scope && r.TargetId == targetId)
            .OrderByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync();
    }
}
