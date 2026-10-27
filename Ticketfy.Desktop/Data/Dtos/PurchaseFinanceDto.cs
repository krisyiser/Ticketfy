namespace Ticketfy.Data.Dtos;

/// <summary>Recorded money and credit, not an assertion that an external bank transfer occurred.</summary>
public sealed record PurchaseFinancialSummary(decimal Payments, decimal Returns, decimal CreditApplied,
    decimal CreditTransferred, decimal CreditRefunded, decimal Balance, decimal ReservedCredit,
    decimal CashPayments, decimal CardPayments, decimal TransferPayments, bool IsHistorical)
{
    public decimal Outstanding => Math.Max(0m, Balance);
    public decimal AvailableCredit => Math.Max(0m, -Balance - ReservedCredit);
    public string Status => IsHistorical ? "Saldo histórico conciliado; pagos anteriores incompletos" :
        Balance > 0 ? "Pendiente de pago" : Balance < 0 ? "Saldo a favor" : "Liquidada";
    public string Methods => string.Join(" / ", new[] { CashPayments > 0 ? "Efectivo" : null,
        CardPayments > 0 ? "Tarjeta" : null, TransferPayments > 0 ? "Transferencia" : null }.Where(s => s is not null));
}

public sealed record PurchaseFinancialEntryDto(long Sequence, string OperationId, string EntryType, decimal Amount,
    string Date, string Method, string Reference, string CashierUserId, string RegisterId, string ShiftId)
{
    public string TypeDisplay => EntryType switch
    {
        "PURCHASE" => "Mercancía recibida",
        "SUPPLIER_PAYMENT" => "Pago a proveedor",
        "SUPPLIER_RETURN" => "Devolución a proveedor",
        "CREDIT_APPLIED_TO" => "Crédito aplicado a esta compra",
        "CREDIT_APPLIED_FROM" => "Crédito trasladado a otra compra",
        "CREDIT_REFUNDED" => "Reembolso recibido",
        "HISTORICAL_BALANCE_ADJUSTMENT" => "Conciliación histórica",
        _ => "Movimiento sin verificar"
    };
    public string DateDisplay => Ticketfy.Core.Helpers.BusinessTime.TryLocal(Date, out var date) ? date.ToString("dd/MM/yyyy HH:mm:ss") : "Fecha sin verificar";
}

public sealed record PurchaseFinancialPage(List<PurchaseFinancialEntryDto> Items, long TotalCount, bool HasMore,
    PurchaseFinancialSummary? Summary, string Warning, PurchaseDto? Document = null);
