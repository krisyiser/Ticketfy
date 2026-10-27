using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Accepted external tender labels at legacy DTO boundaries.</summary>
internal static class TenderMethodCatalog
{
    public static TenderKind Parse(string? method) => method?.Trim() switch
    {
        "Efectivo" or "Cash" => TenderKind.Cash,
        "Tarjeta" or "Card" => TenderKind.Card,
        "Transferencia" or "Transferencia SPEI" or "Transfer" => TenderKind.Transfer,
        _ => throw new InvalidOperationException("Medio de pago no admitido. Seleccione efectivo, tarjeta o transferencia.")
    };

    public static string Label(TenderKind kind) => kind switch
    {
        TenderKind.Cash => "Efectivo",
        TenderKind.Card => "Tarjeta",
        TenderKind.Transfer => "Transferencia",
        _ => throw new InvalidOperationException("Medio de pago externo inválido.")
    };
}
