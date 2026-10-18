using Ticketfy.Core.Helpers;
using System.Threading;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public class PaymentResponse
{
    public string TransactionId { get; set; } = string.Empty;
    public string AuthorizationCode { get; set; } = string.Empty;
    public string CardBrand { get; set; } = string.Empty; // Visa, Mastercard, AMEX
    public string Last4Digits { get; set; } = string.Empty;
}

public interface IPaymentTerminalService
{
    // Crea o recupera una order idempotente en la terminal física.
    Task<Result<PaymentResponse>> ProcessPaymentAsync(decimal amount, string referenceId, CancellationToken cancellationToken);

    // Solicita cancelación usando el ID de order del proveedor.
    Task<Result> CancelPaymentIntentAsync(string referenceId);

    Task<bool> RequiresReconciliationAsync(string referenceId, CancellationToken cancellationToken = default);
}
