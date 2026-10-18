using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models;
using Serilog;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

public partial class SaleService
{
    public Task BeginCheckoutAsync(SaleDto sale) => BeginCheckoutAsync(sale, null);
    public async Task BeginCheckoutAsync(SaleDto sale, Ticketfy.Services.Interfaces.FiscalOutboxRequest? fiscalOutbox)
    {
        string operationId = RequireOperationId(sale.OperationId);
        using var context = await _contextFactory.CreateDbContextAsync();

        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        if (await context.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
            throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        sale = sale with { CashierUserId = actor.UserId, RegisterId = actor.RegisterId, ShiftId = actor.ShiftId };

        var existing = await context.CheckoutOperations.FindAsync(operationId);
        if (existing is not null)
        {
            var previous = JsonSerializer.Deserialize(existing.SalePayload, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)!;
            string? fiscalJson = fiscalOutbox is null ? null : JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest);
            if (CommandIdentity.Sale(previous) != CommandIdentity.Sale(sale) || existing.CashierUserId != actor.UserId
                || existing.ShiftId != actor.ShiftId || existing.FiscalPayload != fiscalJson)
                throw new InvalidOperationException("La clave de operación ya pertenece a otro cobro.");
            return;
        }

        await PrepareSaleAsync(context, sale);

        context.CheckoutOperations.Add(new CheckoutOperationEntity
        {
            OperationId = operationId,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            FiscalPayload = fiscalOutbox is null ? null : JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest),
            FiscalIdempotencyKey = fiscalOutbox?.IdempotencyKey,
            State = CheckoutOperationState.Started,
            PaymentMethod = sale.PaymentMethod,
            Amount = sale.Total,
            SalePayload = JsonSerializer.Serialize(
                sale,
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)
        });
        await context.SaveChangesAsync();
    }

    public async Task MarkPaymentAuthorizedAsync(string operationId, string paymentReference)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        var operation = await context.CheckoutOperations.FindAsync(RequireOperationId(operationId))
            ?? throw new InvalidOperationException("No existe la intención de cobro local.");
        if (operation.CashierUserId != actor.UserId || operation.ShiftId != actor.ShiftId || operation.RegisterId != actor.RegisterId)
            throw new UnauthorizedAccessException("El cobro pertenece a otro operador o turno.");

        if (operation.State == CheckoutOperationState.Completed)
            return;

        var terminal = await context.PaymentTerminalOperations.AsNoTracking()
            .SingleOrDefaultAsync(t => t.ExternalReference == operationId);
        if (terminal is null || terminal.Status != IntegrationOperationStatus.Completed || terminal.ProviderPaymentId != paymentReference)
            throw new InvalidOperationException("La terminal no confirma este pago.");

        operation.State = CheckoutOperationState.PaymentAuthorized;
        operation.PaymentReference = paymentReference;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        operation.FailureReason = null;
        await context.SaveChangesAsync();
    }

    public async Task MarkCheckoutFailedAsync(string operationId, string reason, bool requiresReconciliation)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(context, Ticketfy.Core.Security.PermissionCatalog.PosCheckout);
        var operation = await context.CheckoutOperations.AsNoTracking().SingleOrDefaultAsync(o => o.OperationId == operationId);
        if (operation is not null && (operation.CashierUserId != actor.UserId || operation.ShiftId != actor.ShiftId || operation.RegisterId != actor.RegisterId))
            throw new UnauthorizedAccessException("El cobro pertenece a otro operador o turno.");
        await MarkCheckoutFailedCoreAsync(operationId, reason, requiresReconciliation);
    }
    private async Task MarkCheckoutFailedCoreAsync(string operationId, string reason, bool requiresReconciliation)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var operation = await context.CheckoutOperations.FindAsync(RequireOperationId(operationId));
        if (operation is null || operation.State == CheckoutOperationState.Completed)
            return;

        operation.State = requiresReconciliation
            ? CheckoutOperationState.RequiresReconciliation
            : CheckoutOperationState.Failed;
        operation.FailureReason = reason;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        await context.SaveChangesAsync();
    }

    public async Task<int> RecoverInterruptedCheckoutsAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var interrupted = await context.CheckoutOperations
            .AsNoTracking()
            .Where(o => o.State == CheckoutOperationState.Started
                || o.State == CheckoutOperationState.PaymentAuthorized
                || o.State == CheckoutOperationState.RequiresReconciliation)
            .OrderBy(o => o.CreatedAtUtc)
            .ToListAsync();

        int recovered = 0;
        foreach (var operation in interrupted)
        {
            if (operation.State == CheckoutOperationState.Started)
            {
                var terminal = await context.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(t => t.ExternalReference == operation.OperationId);
                var transfer = await context.TenderConfirmations.AsNoTracking().SingleOrDefaultAsync(t => t.OperationId == operation.OperationId);
                if (terminal?.Status == IntegrationOperationStatus.Completed || transfer is not null)
                {
                    var tracked = await context.CheckoutOperations.FindAsync(operation.OperationId);
                    tracked!.State = CheckoutOperationState.PaymentAuthorized;
                    tracked.PaymentReference = terminal?.ProviderPaymentId ?? transfer!.ExternalReference;
                    await context.SaveChangesAsync();
                }
                else
                {
                    var payload = JsonSerializer.Deserialize(operation.SalePayload, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto);
                    bool cashUncertain = payload is not null && BuildPayments(payload).Any(p => p.Kind == TenderKind.Cash);
                    await MarkCheckoutFailedCoreAsync(operation.OperationId,
                        "La aplicación se cerró antes de confirmar el pago.", cashUncertain || terminal is not null && terminal.Status is not (IntegrationOperationStatus.FailedPermanent or IntegrationOperationStatus.Cancelled));
                    continue;
                }
            }

            if (operation.State == CheckoutOperationState.RequiresReconciliation)
            {
                Log.Warning("Checkout {OperationId} remains blocked until the payment provider is reconciled", operation.OperationId);
                continue;
            }

            try
            {
                var sale = JsonSerializer.Deserialize(
                    operation.SalePayload,
                    Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)
                    ?? throw new InvalidOperationException("El diario de recuperación contiene una venta inválida.");
                if (BuildPayments(sale).Any(p => p.Kind == TenderKind.Cash)
                    && !await context.TransactionJournal.AnyAsync(j => j.OperationId == "RESOLVE:" + operation.OperationId && j.EventType == "CHECKOUT_FUNDS_VERIFIED"))
                    throw new InvalidOperationException("Verifique el efectivo recibido antes de recuperar un cobro mixto.");
                await SaveCoreAsync(sale, recovering: true);
                recovered++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Checkout {OperationId} requires manual reconciliation", operation.OperationId);
                await MarkCheckoutFailedCoreAsync(operation.OperationId, ex.Message, requiresReconciliation: true);
            }
        }

        return recovered;
    }

    public async Task ResolveInterruptedCheckoutAsync(string operationId, bool complete, bool fundsVerified, string reason)
    {
        if (!fundsVerified || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Compruebe el efectivo y el estado bancario; indique el motivo de la resolución.");
        SaleDto payload;
        await using (var db = await _contextFactory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var actor = await AuthorizeAsync(db, Ticketfy.Core.Security.PermissionCatalog.PaymentsReconcile);
            var operation = await db.CheckoutOperations.SingleAsync(o => o.OperationId == operationId);
            if (operation.RegisterId != actor.RegisterId || operation.ShiftId != actor.ShiftId || operation.CashierUserId != actor.UserId)
                throw new UnauthorizedAccessException("Reanude con el operador, caja y turno originales; no se reasignará el cobro.");
            await CommandIdentity.BindAsync(db, actor, "RESOLVE:" + operationId, "CHECKOUT_RESOLVE", operationId, complete, reason);
            if (operation.State == CheckoutOperationState.Completed) return;
            payload = JsonSerializer.Deserialize(operation.SalePayload, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)
                ?? throw new InvalidOperationException("Cobro sin datos recuperables.");
            var terminal = await db.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(t => t.ExternalReference == operationId);
            var transfer = await db.TenderConfirmations.AsNoTracking().SingleOrDefaultAsync(t => t.OperationId == operationId);
            if (!complete)
            {
                if (transfer is not null || terminal is not null && terminal.Status is not (IntegrationOperationStatus.FailedPermanent or IntegrationOperationStatus.Cancelled))
                    throw new InvalidOperationException("Existe un pago bancario confirmado o incierto. No se puede descartar; concilie con el proveedor.");
                operation.State = CheckoutOperationState.Failed;
            }
            else
            {
                foreach (var tender in BuildPayments(payload))
                {
                    if (tender.Kind == TenderKind.Card && (terminal?.Status != IntegrationOperationStatus.Completed || terminal.Amount != tender.Amount || string.IsNullOrWhiteSpace(terminal.ProviderPaymentId)))
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
        await using var db = await _contextFactory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.PaymentsReconcile, false);
        var items = await db.CheckoutOperations.AsNoTracking().Where(o => o.State == CheckoutOperationState.Started
            || o.State == CheckoutOperationState.PaymentAuthorized || o.State == CheckoutOperationState.RequiresReconciliation).ToListAsync();
        return items.Select(o => new PendingCheckoutDto(o.OperationId, o.Amount, o.PaymentMethod, o.State.ToString(), o.RegisterId, o.CashierUserId)).ToList();
    }

    private static string RequireOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new InvalidOperationException("La operación requiere una clave idempotente.");
        return operationId.Trim();
    }
}
