using System.Text;
using System.Text.Json;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.Operations;

/// <summary>Local workload budgets, not a claim of certified business capacity. Never truncate money documents.</summary>
public static class CommercialDocumentLimits
{
    public const int Lines = 1000;
    public const int StockMovements = 10000;
    public const int Tenders = 6;
    public const int IdentifierCharacters = 128;
    public const int TextCharacters = 1024;
    public const int NotesCharacters = 2000;
    public const int TotalTextBytes = 512 * 1024;
    public const int NewPayloadBytes = 1024 * 1024;
    public const int PayloadBytes = 2 * 1024 * 1024;
    public const string LinesMessage = "El documento admite hasta 1.000 partidas. Divida la operación antes de cobrar o recibir mercancía.";
    public const string HistoricalMessage = "El documento supera los límites de lectura o no tiene datos verificables. Conserve su referencia y solicite revisión técnica; no vuelva a cobrar ni mueva mercancía.";

    public static void ValidateSale(SaleDto sale)
    {
        if (sale.Items is null || sale.Items.Count == 0) throw new InvalidOperationException("La venta requiere partidas.");
        RequireLineCount(sale.Items.Count);
        if (sale.Payments?.Count > Tenders) throw new InvalidOperationException("La venta admite hasta seis medios de pago distintos.");
        int total = 0;
        foreach (var id in new[] { sale.Id, sale.OperationId, sale.CustomerId, sale.ShiftId, sale.RegisterId,
            sale.CashierUserId, sale.GiftcardNumber, sale.InvoiceId, sale.UuidSat, sale.SerieFolio, sale.CheckoutInventoryFingerprint })
            Text(id, IdentifierCharacters, ref total);
        foreach (var value in new[] { sale.Date, sale.CancelledAt, sale.PaymentMethod, sale.EstadoFiscal, sale.InvoiceStatus, sale.CashierName })
            Text(value, TextCharacters, ref total);
        foreach (var item in sale.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ProductId)) throw new InvalidOperationException("Partida sin producto verificable.");
            foreach (var id in new[] { item.ProductId, item.AppliedPromotionId, item.SaleLineId, item.SatProductCode, item.SatUnitCode })
                Text(id, IdentifierCharacters, ref total);
            foreach (var value in new[] { item.Name, item.Unit, item.Category }) Text(value, TextCharacters, ref total);
        }
        foreach (var tender in sale.Payments ?? [])
        {
            if (tender is null) throw new InvalidOperationException("Medio de pago sin datos verificables.");
            Text(tender.AccountId, IdentifierCharacters, ref total);
            Text(tender.Reference, 200, ref total);
        }
    }

    public static void ValidatePurchase(PurchaseDto purchase)
    {
        if (purchase.Items is null || purchase.Items.Count == 0) throw new InvalidOperationException("La compra requiere partidas.");
        RequireLineCount(purchase.Items.Count);
        int total = 0;
        foreach (var id in new[] { purchase.Id, purchase.OperationId, purchase.SupplierId }) Text(id, IdentifierCharacters, ref total);
        Text(purchase.InvoiceNumber, 200, ref total); Text(purchase.PaymentReference, 200, ref total);
        Text(purchase.Notes, NotesCharacters, ref total);
        foreach (var value in new[] { purchase.SupplierName, purchase.Date, purchase.DueDate, purchase.PaymentMethod }) Text(value, TextCharacters, ref total);
        foreach (var item in purchase.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ProductId)) throw new InvalidOperationException("Partida de compra sin producto verificable.");
            foreach (var id in new[] { item.Id, item.PurchaseId, item.ProductId }) Text(id, IdentifierCharacters, ref total);
            Text(item.ProductName, TextCharacters, ref total);
        }
    }

    public static void RequireLineCount(int count)
    {
        if (count > Lines) throw new InvalidOperationException(LinesMessage);
    }

    public static void RequireStockMovementCount(int count)
    {
        if (count > StockMovements) throw new InvalidOperationException("La venta produciría más de 10.000 movimientos de inventario al expandir los combos. Divida la operación antes de cobrar.");
    }

    private static void Text(string? value, int maximum, ref int total)
    {
        if (value is null) return;
        if (value.Length > maximum) throw new InvalidOperationException($"Un campo del documento supera {maximum:N0} caracteres. Corrija el documento antes de continuar.");
        if (value.Contains('\0')) throw new InvalidOperationException("El documento contiene un carácter nulo no admitido. Corrija el texto antes de continuar.");
        total = checked(total + Encoding.UTF8.GetByteCount(value));
        if (total > TotalTextBytes) throw new InvalidOperationException("El texto conjunto del documento supera 512 KiB en UTF-8. Divida la operación antes de continuar.");
    }

    public static string RequirePayload(string payload)
    {
        // Length is a cheap first check; UTF-8 bytes enforce the actual storage/read budget.
        if (payload.Length > PayloadBytes || Encoding.UTF8.GetByteCount(payload) > PayloadBytes)
            throw new InvalidOperationException(HistoricalMessage);
        return payload;
    }

    public static string RequireNewPayload(string payload)
    {
        // The read/storage ceiling leaves room for generated line IDs, cost and tax evidence.
        if (payload.Length > NewPayloadBytes || Encoding.UTF8.GetByteCount(payload) > NewPayloadBytes)
            throw new InvalidOperationException("El documento nuevo supera 1 MiB serializado. Divida la operación antes de cobrar.");
        return payload;
    }

    internal static SaleDto ReadSale(string payload)
    {
        RequirePayload(payload);
        if (payload.Length == 0) throw new InvalidOperationException(HistoricalMessage);
        var sale = JsonSerializer.Deserialize(payload, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto)
            ?? throw new InvalidOperationException(HistoricalMessage);
        ValidateSale(sale);
        return sale;
    }
}
