using Ticketfy.Core.Helpers;

namespace Ticketfy.Services.Interfaces;

public sealed record TerminalReconciliationResult(string OperationReference, string ProviderOrderId,
    string Status, bool CanComplete, bool NoFundsConfirmed);

public interface IPaymentTerminalReconciliation
{
    // Queries an existing provider order only; never creates, retries or cancels a charge.
    Task<Result<TerminalReconciliationResult>> RefreshPaymentAsync(string referenceId,
        string? providerOrderId = null, CancellationToken cancellationToken = default);
}
