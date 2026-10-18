using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ticketfy.ViewModels.Dialogs.Checkout;

/// <summary>
/// Builds and validates CFDI 4.0 payloads. Network access is intentionally not
/// available here; every issue request must pass through the durable outbox.
/// </summary>
public static class CheckoutInvoiceHandler
{
    public static (FacturamaCfdiRequest? request, string? errorMessage) BuildInvoiceRequest(
        string rfc,
        string razonSocial,
        string usoCfdi,
        string regime,
        string zipCode,
        List<SaleItemSnapshotDto> itemSnapshots)
    {
        var request = BuildRequest(rfc, razonSocial, usoCfdi, regime, zipCode, itemSnapshots);
        if (!Regex.IsMatch(request.Receiver.Rfc, @"^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$", RegexOptions.IgnoreCase))
            return (null, "El RFC ingresado no tiene un formato válido.");
        if (!Regex.IsMatch(request.Receiver.TaxZipCode, @"^[0-9]{5}$"))
            return (null, "El código postal fiscal debe contener exactamente 5 dígitos.");
        if (request.Items.Exists(item => !Regex.IsMatch(item.ProductCode, @"^[0-9]{8}$")))
            return (null, "Todos los productos deben tener una clave SAT de 8 dígitos.");
        if (request.Items.Exists(item => string.IsNullOrWhiteSpace(item.UnitCode)))
            return (null, "Todos los productos deben tener una clave de unidad SAT.");
        return (request, null);
    }

    private static FacturamaCfdiRequest BuildRequest(
        string rfc,
        string razonSocial,
        string usoCfdi,
        string regime,
        string zipCode,
        List<SaleItemSnapshotDto> itemSnapshots)
    {
        var cfdiRequest = new FacturamaCfdiRequest
        {
            Receiver = new CfdiReceiver
            {
                Rfc = rfc.Trim(),
                Name = razonSocial.Trim(),
                CfdiUse = usoCfdi.Split('-')[0].Trim(),
                FiscalRegime = regime.Split('-')[0].Trim(),
                TaxZipCode = zipCode.Trim()
            },
            PaymentForm = "01",
            PaymentMethod = "PUE",
            ExpeditionPlace = string.Empty
        };

        foreach (var item in itemSnapshots)
        {
            decimal priceWithIva = (decimal)item.UnitPrice;
            decimal basePrice = Math.Round(priceWithIva / 1.16m, 6);
            decimal subtotal = Math.Round(basePrice * (decimal)item.Quantity, 2);
            decimal totalTax = Math.Round(subtotal * 0.16m, 2);

            cfdiRequest.Items.Add(new CfdiItem
            {
                ProductCode = item.SatProductCode,
                IdentificationNumber = item.ProductId,
                Description = item.Name,
                Unit = item.Unit,
                UnitCode = item.SatUnitCode,
                UnitPrice = Math.Round(basePrice, 2),
                Quantity = (decimal)item.Quantity,
                Subtotal = subtotal,
                TaxObject = "02",
                Total = subtotal + totalTax,
                Taxes = new List<CfdiTax>
                {
                    new CfdiTax
                    {
                        Name = "IVA",
                        IsRetention = false,
                        Rate = 0.16m,
                        Total = totalTax,
                        Base = subtotal,
                        TaxObject = "02"
                    }
                }
            });
        }

        return cfdiRequest;
    }
}
