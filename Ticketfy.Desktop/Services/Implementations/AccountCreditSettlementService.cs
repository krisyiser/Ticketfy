using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public sealed record AccountDocumentBalance(string DocumentId, decimal Balance, string Date)
{
    public string Label => $"{DocumentId} · {(Balance < 0 ? "A favor" : "Pendiente")} {Math.Abs(Balance):C2}";
}

/// <summary>Explicit document-to-document credit allocation and verified refunds; never initiates a bank charge.</summary>
public sealed partial class AccountCreditSettlementService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations)
{
    public async Task<List<AccountDocumentBalance>> GetDocumentsAsync(AccountKind kind, string account)
    {
        var page = await GetDocumentPagesAsync(kind, account);
        if (page.HasMoreSources || page.HasMoreTargets) throw new InvalidOperationException("Utilice las páginas de documentos de la cuenta; la lista completa excede el límite de lectura.");
        return page.Sources.Concat(page.Targets).OrderBy(d => d.Date).ThenBy(d => d.DocumentId).ToList();
    }

    public Task ApplyAsync(AccountKind kind, string account, string source, string target, decimal amount, string reason, string operationId) =>
        SettleAsync(kind, account, source, target, amount, reason, operationId, null, null, false);

    // Compatibility for already-observed refunds. Interactive callers prepare BEFORE moving funds.
    public async Task RefundAsync(AccountKind kind, string account, string source, decimal amount, string method, string? reference,
        string reason, string operationId, bool fundsVerified)
    {
        if (!fundsVerified) throw new InvalidOperationException("Verifique los fondos antes de registrar el reembolso.");
        await PrepareRefundAsync(kind, account, source, amount, method, reason, operationId);
        await ResolveRefundAsync(operationId, true, reference, reason, true);
    }

    private static void ValidateAccountKind(AccountKind kind)
    {
        if (kind is not (AccountKind.Customer or AccountKind.Supplier)) throw new InvalidOperationException("Sólo se admiten cuentas de clientes o proveedores.");
    }

    private async Task SettleAsync(AccountKind kind, string account, string source, string? target, decimal amount,
        string reason, string operationId, TenderKind? tender, string? reference, bool verified)
    {
        ValidateAccountKind(kind);
        operationId = CommercialLedger.OperationId(operationId);
        bool refund = tender.HasValue;
        if (amount <= 0 || CommercialLedger.Money(amount) != amount || string.IsNullOrWhiteSpace(reason) || reason.Length > 500
            || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Indique cuenta, documento, importe positivo de hasta dos decimales y motivo de hasta 500 caracteres.");
        if (!refund && (string.IsNullOrWhiteSpace(target) || source == target))
            throw new InvalidOperationException("Seleccione otro documento pendiente de la misma cuenta.");
        if (refund && !verified) throw new InvalidOperationException("Verifique la entrega al cliente o la recepción del proveedor antes de registrar el reembolso. Esta acción no mueve dinero bancario.");
        reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim().ToUpperInvariant();
        if (refund && tender != Data.Entities.TenderKind.Cash && (reference is null || reference.Length > 200))
            throw new InvalidOperationException("Indique la referencia única del reembolso bancario confirmado (máximo 200 caracteres).");
        if (tender == Data.Entities.TenderKind.Cash) reference = null;
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await operations.RequireAsync(db, PermissionCatalog.AccountCreditSettle);
        if (refund && tender != Data.Entities.TenderKind.Cash) await operations.RequireAsync(db, PermissionCatalog.PaymentsReconcile);
        await PostSettlementAsync(db, actor, kind, account, source, target, amount, reason, operationId, tender, reference, null);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task PostSettlementAsync(AppDbContext db, OperationIdentity actor, AccountKind kind, string account, string source,
        string? target, decimal amount, string reason, string operationId, TenderKind? tender, string? reference, string? requestId)
    {
        bool refund = tender.HasValue;
        await CommandIdentity.BindAsync(db, actor, operationId, "ACCOUNT_CREDIT_SETTLEMENT", kind, account, source, target, amount, reason.Trim(), tender, reference);
        if (await db.AccountCreditSettlements.AnyAsync(s => s.OperationId == operationId)) return;
        var customer = await ValidateCreditAsync(db, kind, account, source, target, amount, refund, requestId);
        if (reference is not null) await BankReferenceGuard.RequireUnusedAsync(db, reference);

        db.AccountCreditSettlements.Add(new AccountCreditSettlementEntity
        {
            OperationId = operationId,
            AccountKind = kind,
            AccountId = account,
            SourceDocumentId = source,
            TargetDocumentId = target,
            Amount = amount,
            Kind = refund ? AccountCreditSettlementKind.Refund : AccountCreditSettlementKind.Application,
            TenderKind = tender,
            ExternalReference = reference,
            Reason = reason.Trim(),
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId!
        });
        CommercialLedger.Account(db, actor, operationId, kind, account, source, amount, refund ? "CREDIT_REFUNDED" : "CREDIT_APPLIED_FROM");
        if (!refund) CommercialLedger.Account(db, actor, operationId, kind, account, target!, -amount, "CREDIT_APPLIED_TO");
        else
        {
            if (customer is not null) customer.Debt += amount;
            if (tender == Data.Entities.TenderKind.Cash)
                await CommercialLedger.CashAsync(db, actor, operationId, operationId, amount, kind == AccountKind.Customer,
                    kind == AccountKind.Customer ? MovementType.ReembolsoSaldoCliente : MovementType.ReembolsoProveedor, "Reembolso de saldo a favor documentado");
        }
        CommercialLedger.Journal(db, actor, operationId, refund ? "ACCOUNT_CREDIT_REFUNDED" : "ACCOUNT_CREDIT_APPLIED", operationId, amount,
            refund ? "Reembolso verificado; no se inicia un cobro bancario." : "Aplicación entre documentos, sin movimiento de dinero.");
    }

    private static async Task<CustomerEntity?> ValidateCreditAsync(AppDbContext db, AccountKind kind, string account, string source,
        string? target, decimal amount, bool refund, string? requestId)
    {
        decimal total = 0m, sourceBalance = 0m, targetBalance = 0m, reserved = 0m;
        bool opening = false;
        await foreach (var entry in db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == kind && e.AccountId == account)
            .Select(e => new { e.Amount, Source = e.DocumentId == source, Target = e.DocumentId == target, Opening = e.EntryType == "MIGRATION_OPENING" }).AsAsyncEnumerable())
        {
            total += entry.Amount;
            if (entry.Source) sourceBalance += entry.Amount;
            if (entry.Target) targetBalance += entry.Amount;
            opening |= entry.Opening;
        }
        await foreach (var value in PendingRequests(db).Where(r => r.AccountKind == kind && r.AccountId == account
            && r.SourceDocumentId == source && r.OperationId != requestId).Select(r => r.Amount).AsAsyncEnumerable()) reserved += value;
        if (-sourceBalance - reserved < amount)
            throw new InvalidOperationException("El documento de origen no tiene suficiente saldo a favor disponible.");
        if (!refund && targetBalance < amount)
            throw new InvalidOperationException("El documento de destino no tiene suficiente deuda en esta cuenta.");
        CustomerEntity? customer = null;
        if (kind == AccountKind.Customer)
        {
            customer = await db.Customers.SingleAsync(c => c.Id == account);
            if (customer.Debt != total) throw new InvalidOperationException("Concilie el saldo del cliente antes de liquidar créditos.");
            if (opening
                && !await db.HistoricalBalanceReconciliations.AnyAsync(r => r.Scope == HistoricalBalanceScope.CustomerAccount && r.TargetId == account))
                throw new InvalidOperationException("El saldo histórico requiere conciliación documentada.");
        }
        else
        {
            if (!await db.Suppliers.AnyAsync(s => s.Id == account)) throw new InvalidOperationException("Proveedor no encontrado.");
            var documentIds = new[] { source, target };
            var legacy = await db.Purchases.Where(p => documentIds.Contains(p.Id) && p.LedgerVersion == 0).Select(p => p.Id).ToListAsync();
            foreach (var id in legacy)
                if (!await db.HistoricalBalanceReconciliations.AnyAsync(r => r.Scope == HistoricalBalanceScope.SupplierPurchase && r.TargetId == id))
                    throw new InvalidOperationException("Concilie primero la compra histórica.");
        }
        return customer;
    }
}
