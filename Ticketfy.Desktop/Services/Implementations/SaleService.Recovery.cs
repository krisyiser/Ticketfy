using Microsoft.EntityFrameworkCore;
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
    public async Task BeginCheckoutAsync(SaleDto sale)
    {
        string operationId = RequireOperationId(sale.OperationId);
        using var context = await _contextFactory.CreateDbContextAsync();

        var existing = await context.CheckoutOperations.FindAsync(operationId);
        if (existing is not null)
        {
            if (existing.State == CheckoutOperationState.Completed)
                return;

            string currentPayload = JsonSerializer.Serialize(
                sale,
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto);
            if (!string.Equals(existing.SalePayload, currentPayload, StringComparison.Ordinal))
                throw new InvalidOperationException("La clave de operación ya pertenece a otro cobro.");
            return;
        }

        context.CheckoutOperations.Add(new CheckoutOperationEntity
        {
            OperationId = operationId,
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
        var operation = await context.CheckoutOperations.FindAsync(RequireOperationId(operationId))
            ?? throw new InvalidOperationException("No existe la intención de cobro local.");

        if (operation.State == CheckoutOperationState.Completed)
            return;

        operation.State = CheckoutOperationState.PaymentAuthorized;
        operation.PaymentReference = paymentReference;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        operation.FailureReason = null;
        await context.SaveChangesAsync();
    }

    public async Task MarkCheckoutFailedAsync(string operationId, string reason, bool requiresReconciliation)
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
                await MarkCheckoutFailedAsync(
                    operation.OperationId,
                    "La aplicación se cerró antes de confirmar el pago.",
                    requiresReconciliation: false);
                continue;
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
                await SaveAsync(sale);
                recovered++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Checkout {OperationId} requires manual reconciliation", operation.OperationId);
                await MarkCheckoutFailedAsync(operation.OperationId, ex.Message, requiresReconciliation: true);
            }
        }

        return recovered;
    }

    private static string RequireOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new InvalidOperationException("La operación requiere una clave idempotente.");
        return operationId.Trim();
    }
}
