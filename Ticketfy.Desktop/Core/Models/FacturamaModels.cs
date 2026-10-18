using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ticketfy.Core.Models;

public class FacturamaCfdiRequest
{
    [JsonPropertyName("IdCfdi")] public string? IdCfdi { get; set; }
    [JsonPropertyName("Folio")] public string? Folio { get; set; }
    [JsonPropertyName("OrderNumber")] public string? OrderNumber { get; set; }
    [JsonPropertyName("Receiver")] public CfdiReceiver Receiver { get; set; } = new();
    [JsonPropertyName("CfdiType")] public string CfdiType { get; set; } = "I";
    [JsonPropertyName("PaymentForm")] public string PaymentForm { get; set; } = "01";
    [JsonPropertyName("PaymentMethod")] public string PaymentMethod { get; set; } = "PUE";
    [JsonPropertyName("Currency")] public string Currency { get; set; } = "MXN";
    [JsonPropertyName("Exportation")] public string Exportation { get; set; } = "01";
    [JsonPropertyName("ExpeditionPlace")] public string ExpeditionPlace { get; set; } = "";
    [JsonPropertyName("Items")] public List<CfdiItem> Items { get; set; } = new();
}

public class CfdiReceiver
{
    [JsonPropertyName("Rfc")] public string Rfc { get; set; } = "";
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("CfdiUse")] public string CfdiUse { get; set; } = "G03";
    [JsonPropertyName("FiscalRegime")] public string FiscalRegime { get; set; } = "616";
    [JsonPropertyName("TaxZipCode")] public string TaxZipCode { get; set; } = "";
}

public class CfdiItem
{
    [JsonPropertyName("ProductCode")] public string ProductCode { get; set; } = ""; // Viene del DB local
    [JsonPropertyName("IdentificationNumber")] public string IdentificationNumber { get; set; } = "";
    [JsonPropertyName("Description")] public string Description { get; set; } = "";
    [JsonPropertyName("Unit")] public string Unit { get; set; } = "Pieza";
    [JsonPropertyName("UnitCode")] public string UnitCode { get; set; } = ""; // Viene del DB local
    [JsonPropertyName("UnitPrice")] public decimal UnitPrice { get; set; }
    [JsonPropertyName("Quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("Subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("Discount")] public decimal Discount { get; set; }
    [JsonPropertyName("TaxObject")] public string TaxObject { get; set; } = "02";
    [JsonPropertyName("Total")] public decimal Total { get; set; }
    [JsonPropertyName("Taxes")] public List<CfdiTax> Taxes { get; set; } = new();
}

public class CfdiTax
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "IVA";
    [JsonPropertyName("IsRetention")] public bool IsRetention { get; set; } = false;
    [JsonPropertyName("IsQuota")] public bool IsQuota { get; set; }
    [JsonPropertyName("TaxObject")] public string TaxObject { get; set; } = "02";
    [JsonPropertyName("Rate")] public decimal Rate { get; set; } = 0.16m;
    [JsonPropertyName("Total")] public decimal Total { get; set; }
    [JsonPropertyName("Base")] public decimal Base { get; set; }
}

public class FacturamaCfdiResponse
{
    [JsonPropertyName("Id")] public string Id { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("Serie")] public string Serie { get; set; } = "";
    [JsonPropertyName("Folio")] public string Folio { get; set; } = "";
    [JsonPropertyName("Uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("IsActive")] public bool IsActive { get; set; }
    [JsonPropertyName("Complement")] public FacturamaComplement? Complement { get; set; }

    [JsonIgnore]
    public string SatUuid => !string.IsNullOrWhiteSpace(Uuid)
        ? Uuid
        : Complement?.TaxStamp?.Uuid ?? string.Empty;
}

public class FacturamaComplement
{
    [JsonPropertyName("TaxStamp")] public FacturamaTaxStamp? TaxStamp { get; set; }
}

public class FacturamaTaxStamp
{
    [JsonPropertyName("Uuid")] public string Uuid { get; set; } = "";
}

public class FacturamaFileResponse
{
    [JsonPropertyName("ContentEncoding")] public string ContentEncoding { get; set; } = "";
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "";
    [JsonPropertyName("ContentLength")] public long ContentLength { get; set; }
    [JsonPropertyName("Content")] public string Content { get; set; } = "";
}

public class FacturamaCancellationResponse
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("Uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("Message")] public string Message { get; set; } = "";
    [JsonPropertyName("RequestDate")] public string RequestDate { get; set; } = "";
    [JsonPropertyName("ResponseDate")] public string ResponseDate { get; set; } = "";
    [JsonPropertyName("ExpirationDate")] public string ExpirationDate { get; set; } = "";
    [JsonPropertyName("AcuseXmlBase64")] public string AcuseXmlBase64 { get; set; } = "";
}

[JsonSerializable(typeof(FacturamaCfdiRequest))]
[JsonSerializable(typeof(FacturamaCfdiResponse))]
[JsonSerializable(typeof(FacturamaFileResponse))]
[JsonSerializable(typeof(FacturamaCancellationResponse))]
public partial class FacturamaJsonContext : JsonSerializerContext { }
