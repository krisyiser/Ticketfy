using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models;
using Serilog;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Data;

namespace Ticketfy.Services.Implementations;

public partial class SaleService
{
    public Task BeginCheckoutAsync(SaleDto sale) => BeginCheckoutAsync(sale, null);
    public async Task BeginCheckoutAsync(SaleDto sale, Ticketfy.Services.Interfaces.FiscalOutboxRequest? fiscalOutbox)
    {
        CommercialDocumentLimits.ValidateSale(sale);
        string operationId = RequireOperationId(sale.OperationId);
        using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        if (await context.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
            throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        sale = sale with { CashierUserId = actor.UserId, RegisterId = actor.RegisterId, ShiftId = actor.ShiftId };

        var existing = await CheckoutDocumentQuery.Bounded(context).SingleOrDefaultAsync(o => o.OperationId == operationId);
        if (existing is not null)
        {
            var previous = CommercialDocumentLimits.ReadSale(existing.SalePayload);
            string? fiscalJson = fiscalOutbox is null ? null : JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest);
            if (CommandIdentity.Sale(previous) != CommandIdentity.Sale(sale) || existing.CashierUserId != actor.UserId
                || existing.ShiftId != actor.ShiftId || existing.FiscalPayload != fiscalJson)
                throw new InvalidOperationException("La clave de operación ya pertenece a otro cobro.");
            if (existing.State == CheckoutOperationState.Failed) throw new InvalidOperationException(CheckoutStatePolicy.ClosedIntent);
            if (existing.State == CheckoutOperationState.RequiresReconciliation || !Enum.IsDefined(existing.State))
                throw new InvalidOperationException(CheckoutStatePolicy.ReconciliationRequired);
            return;
        }

        await CheckoutStatePolicy.RequireNoOtherPendingAsync(context, actor.ShiftId, operationId);
        var prepared = await PrepareSaleAsync(context, sale);
        sale = sale with { CheckoutInventoryFingerprint = prepared.Graph.CheckoutFingerprint(sale.Items.Select(i => i.ProductId)) };

        context.CheckoutOperations.Add(new CheckoutOperationEntity
        {
            OperationId = operationId,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            FiscalPayload = fiscalOutbox is null ? null : CommercialDocumentLimits.RequireNewPayload(JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest)),
            FiscalIdempotencyKey = fiscalOutbox?.IdempotencyKey,
            State = CheckoutOperationState.Started,
            PaymentMethod = sale.PaymentMethod,
            Amount = sale.Total,
            SalePayload = CommercialDocumentLimits.RequireNewPayload(JsonSerializer.Serialize(
                sale,
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto))
        });
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task MarkPaymentAuthorizedAsync(string operationId, string paymentReference)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        var operation = await CheckoutDocumentQuery.Bounded(context).SingleOrDefaultAsync(o => o.OperationId == RequireOperationId(operationId))
            ?? throw new InvalidOperationException("No existe la intención de cobro local.");
        if (operation.CashierUserId != actor.UserId || operation.ShiftId != actor.ShiftId || operation.RegisterId != actor.RegisterId)
            throw new UnauthorizedAccessException("El cobro pertenece a otro operador o turno.");

        if (operation.State == CheckoutOperationState.Completed)
            return;
        if (operation.State == CheckoutOperationState.Failed) throw new InvalidOperationException(CheckoutStatePolicy.ClosedIntent);
        if (operation.State == CheckoutOperationState.RequiresReconciliation || !Enum.IsDefined(operation.State))
            throw new InvalidOperationException(CheckoutStatePolicy.ReconciliationRequired);

        var terminal = await context.PaymentTerminalOperations.AsNoTracking()
            .SingleOrDefaultAsync(t => t.ExternalReference == operationId);
        if (!TerminalOrderEvidence.IsCaptured(terminal) || terminal!.ProviderPaymentId != paymentReference)
            throw new InvalidOperationException("La terminal no confirma este pago.");

        operation.State = CheckoutOperationState.PaymentAuthorized;
        operation.PaymentReference = paymentReference;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        operation.FailureReason = null;
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<CheckoutOperationState?> MarkCheckoutFailedAsync(string operationId, string reason, bool requiresReconciliation)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        var operation = await CheckoutDocumentQuery.Bounded(context).AsNoTracking().SingleOrDefaultAsync(o => o.OperationId == operationId);
        if (operation is not null && (operation.CashierUserId != actor.UserId || operation.ShiftId != actor.ShiftId || operation.RegisterId != actor.RegisterId))
            throw new UnauthorizedAccessException("El cobro pertenece a otro operador o turno.");
        var result = await UpdateCheckoutFailureAsync(context, operationId, reason, requiresReconciliation);
        await transaction.CommitAsync();
        return result;
    }
    private async Task MarkCheckoutFailedCoreAsync(string operationId, string reason, bool requiresReconciliation)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await UpdateCheckoutFailureAsync(context, operationId, reason, requiresReconciliation);
        await transaction.CommitAsync();
    }
    private static async Task<CheckoutOperationState?> UpdateCheckoutFailureAsync(AppDbContext context, string operationId, string reason, bool requiresReconciliation)
    {
        var operation = await CheckoutDocumentQuery.Bounded(context).SingleOrDefaultAsync(o => o.OperationId == RequireOperationId(operationId));
        if (operation is null || operation.State == CheckoutOperationState.Completed)
            return operation?.State;

        // Recognizing a committed result needs its identity, never the historic ticket JSON.
        var committed = await context.Sales.AsNoTracking().Where(s => s.OperationId == operationId)
            .Select(s => new { s.Id }).SingleOrDefaultAsync();
        if (committed is not null)
        {
            operation.State = CheckoutOperationState.Completed;
            operation.SaleId = committed.Id;
            operation.FailureReason = null;
        }
        else
        {
            bool bankEvidence = await CheckoutStatePolicy.HasBankEvidenceAsync(context, operationId);
            bool noFundsVerified = await context.TransactionJournal.AnyAsync(j => j.OperationId == "RESOLVE:" + operationId &&
                j.EventType == "CHECKOUT_NO_FUNDS_VERIFIED" && j.EntityId == operationId &&
                j.RegisterId == operation.RegisterId && j.ShiftId == operation.ShiftId);
            bool cashOrInvalidPayload;
            try
            {
                var payload = CommercialDocumentLimits.ReadSale(operation.SalePayload);
                cashOrInvalidPayload = BuildPayments(payload).Any(p => p.Kind == TenderKind.Cash);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { cashOrInvalidPayload = true; }
            bool uncertain = bankEvidence || !noFundsVerified && (requiresReconciliation || cashOrInvalidPayload ||
                operation.State is CheckoutOperationState.RequiresReconciliation or CheckoutOperationState.PaymentAuthorized || !Enum.IsDefined(operation.State));
            operation.State = uncertain ? CheckoutOperationState.RequiresReconciliation : CheckoutOperationState.Failed;
            operation.FailureReason = reason;
        }
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        await context.SaveChangesAsync();
        return operation.State;
    }

    public async Task ResolveInterruptedCheckoutAsync(string operationId, bool complete, bool fundsVerified, string reason)
    {
        if (!fundsVerified || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Compruebe el efectivo y el estado bancario; indique el motivo de la resolución.");
        SaleDto payload;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var approver = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(
                db, Ticketfy.Core.Security.PermissionCatalog.CheckoutRecover, false);
            var operation = await CheckoutDocumentQuery.Bounded(db).SingleAsync(o => o.OperationId == operationId);
            if (operation.RegisterId != approver.RegisterId)
                throw new UnauthorizedAccessException("Inicie sesión como supervisor en la caja original para resolver el cobro.");
            // The supervisor approves the original shift without taking ownership of it.
            var actor = approver with { ShiftId = operation.ShiftId };
            await CommandIdentity.BindAsync(db, actor, "RESOLVE:" + operationId, "CHECKOUT_RESOLVE", operationId, complete, reason);
            if (operation.State == CheckoutOperationState.Completed) return;
            if (operation.State == CheckoutOperationState.Failed && !complete && !await CheckoutStatePolicy.HasBankEvidenceAsync(db, operationId)) return;
            if (operation.State == CheckoutOperationState.Failed && complete && !await CheckoutStatePolicy.HasBankEvidenceAsync(db, operationId))
                throw new InvalidOperationException(CheckoutStatePolicy.ClosedIntent);
            var originalShift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == operation.ShiftId
                && s.RegisterId == operation.RegisterId && s.CashierUserId == operation.CashierUserId && s.IsOpen == 1);
            if (originalShift is null) throw new InvalidOperationException("El turno original debe permanecer abierto para resolver el cobro.");
            payload = CommercialDocumentLimits.ReadSale(operation.SalePayload);
            if (payload.CashierUserId != operation.CashierUserId || payload.ShiftId != operation.ShiftId
                || payload.RegisterId != operation.RegisterId || payload.Total != operation.Amount)
                throw new InvalidOperationException("La intención no coincide con la identidad o importe del cobro original.");
            var terminal = await db.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(t => t.ExternalReference == operationId);
            var transfer = await db.TenderConfirmations.AsNoTracking().SingleOrDefaultAsync(t => t.OperationId == operationId);
            if (!complete)
            {
                if (transfer is not null || terminal is not null && !TerminalOrderEvidence.NoFunds(terminal))
                    throw new InvalidOperationException("Existe un pago bancario confirmado o incierto. No se puede descartar; concilie con el proveedor.");
                operation.State = CheckoutOperationState.Failed;
            }
            else
            {
                foreach (var tender in BuildPayments(payload))
                {
                    if (tender.Kind == TenderKind.Card && (!TerminalOrderEvidence.IsCaptured(terminal) || terminal!.Amount != tender.Amount))
                        throw new InvalidOperationException("Falta confirmación de terminal por el importe exacto.");
                    if (tender.Kind == TenderKind.Transfer && (transfer is null || transfer.Amount != tender.Amount))
                        throw new InvalidOperationException("Falta la confirmación de transferencia.");
                }
                operation.State = CheckoutOperationState.PaymentAuthorized;
            }
            operation.FailureReason = null; operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            if (!await db.TransactionJournal.AnyAsync(j => j.OperationId == "RESOLVE:" + operationId))
                CommercialLedger.Journal(db, actor, "RESOLVE:" + operationId, complete ? "CHECKOUT_FUNDS_VERIFIED" : "CHECKOUT_NO_FUNDS_VERIFIED", operationId, payload.Total, reason);
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        if (complete) await SaveCoreAsync(payload, recovering: true);
    }
    public async Task<List<PendingCheckoutDto>> GetInterruptedCheckoutsAsync()
    {
        var result = await GetInterruptedCheckoutsPageAsync(LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(result.Next is not null);
        return result.Items;
    }

    public async Task<PendingCheckoutPage> GetInterruptedCheckoutsPageAsync(int size = 100, string? search = null,
        PendingCheckoutCursor? after = null)
    {
        if (size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(size));
        if (search?.Any(char.IsControl) == true)
            throw new ArgumentException("La búsqueda no admite caracteres de control.", nameof(search));
        search = search?.Trim() ?? "";
        if (search.Length > 128)
            throw new ArgumentException("La búsqueda admite hasta 128 caracteres sin controles.", nameof(search));
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db,
            Ticketfy.Core.Security.PermissionCatalog.CheckoutRecover, false);
        if (after is not null && (after.RegisterId != actor.RegisterId || after.Search != search ||
            string.IsNullOrEmpty(after.AfterOperationId)))
            throw new ArgumentException("La posición pertenece a otra caja o búsqueda. Vuelva a consultar.", nameof(after));
        var query = CheckoutStatePolicy.Pending(db).AsNoTracking().Where(o => o.RegisterId == actor.RegisterId);
        if (search.Length > 0) query = query.Where(o => o.OperationId.Contains(search));
        long count = await query.LongCountAsync();
        if (after is not null) query = query.Where(o => string.Compare(EF.Functions.Collate(o.OperationId, "BINARY"), after.AfterOperationId) > 0);
        var rows = await query.OrderBy(o => EF.Functions.Collate(o.OperationId, "BINARY"))
            .Select(o => new PendingCheckoutDto(o.OperationId, o.Amount, o.PaymentMethod, o.State.ToString(), o.RegisterId, o.CashierUserId))
            .Take(size + 1).ToListAsync();
        bool more = rows.Count > size;
        if (more) rows.RemoveAt(size);
        if ((await _operations.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.CheckoutRecover, false)).UserId != actor.UserId)
            throw new UnauthorizedAccessException("La sesión cambió durante la consulta. Vuelva a consultar.");
        return new(rows, count, more ? new(actor.RegisterId, search, rows[^1].OperationId) : null);
    }

    // Structural validity only; prices, funds, attribution and stock are still
    // verified by the transactional sale path before publishing any money.
    private static bool HasCheckoutPayloadStructure([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] SaleDto? sale) =>
        sale?.Items is { Count: > 0 } && !sale.Items.Any(item => item is null) &&
        sale.Payments?.Any(payment => payment is null) != true;

    private static string RequireOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new InvalidOperationException("La operación requiere una clave idempotente.");
        return operationId.Trim();
    }
}
