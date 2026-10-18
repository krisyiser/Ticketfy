using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

using Ticketfy.Core.Helpers;
using Ticketfy.Core.Enums;
using System.Text.Json.Serialization;

namespace Ticketfy.Data.Dtos;

[method: Dapper.ExplicitConstructor]
public record ProductDto(
    string Id,
    string? Barcode,
    string Name,
    decimal Cost,
    decimal Price,
    decimal WholesalePrice = 0m,
    int WholesaleThreshold = 0,
    double Stock = 0.0,
    string Category = "General",
    string Unit = "Pza",
    int ExpiresSoon = 0,
    string? CreatedAt = null,
    double PointsRewarded = 0.0,
    double ReorderQuantity = 10.0,
    string LocationRack = "Pasillo 1 - Anaquel A",
    string SatProductCode = "01010101",
    string SatUnitCode = "H87",
    double MinStock = 5.0,
    string? DefaultSupplierId = null,
    bool IsBulk = false,
    bool IsKit = false
)
{
    public ProductDto(string Id, string? Barcode, string Name, double Cost, double Price,
        double WholesalePrice = 0d, int WholesaleThreshold = 0, double Stock = 0d,
        string Category = "General", string Unit = "Pza", int ExpiresSoon = 0,
        string? CreatedAt = null, double PointsRewarded = 0d, double ReorderQuantity = 10d,
        string LocationRack = "Pasillo 1 - Anaquel A", string SatProductCode = "01010101",
        string SatUnitCode = "H87", double MinStock = 5d, string? DefaultSupplierId = null,
        bool IsBulk = false, bool IsKit = false)
        : this(Id, Barcode, Name, Convert.ToDecimal(Cost), Convert.ToDecimal(Price),
            Convert.ToDecimal(WholesalePrice), WholesaleThreshold, Stock, Category, Unit,
            ExpiresSoon, CreatedAt, PointsRewarded, ReorderQuantity, LocationRack,
            SatProductCode, SatUnitCode, MinStock, DefaultSupplierId, IsBulk, IsKit)
    { }

    public decimal CostPrice => Cost;
    public decimal SalePrice => Price;
    public bool IsOutOfStock => Stock <= 0.0;
    public bool IsAvailable => Stock > 0.0;
}

public record CustomerDto(
    string Id,
    string Nombre,
    string Telefono,
    string Email,
    string Rfc,
    decimal LimiteCredito,
    decimal Deuda = 0m,
    double PuntosSaldo = 0.0,
    decimal CurrentBalance = 0m,
    string CustomerCode = ""
)
{
    public CustomerDto(string Id, string Nombre, string Telefono, string Email, string Rfc,
        double LimiteCredito, double Deuda = 0d, double PuntosSaldo = 0d,
        double CurrentBalance = 0d, string CustomerCode = "")
        : this(Id, Nombre, Telefono, Email, Rfc, Convert.ToDecimal(LimiteCredito),
            Convert.ToDecimal(Deuda), PuntosSaldo, Convert.ToDecimal(CurrentBalance), CustomerCode)
    { }

    public string Name => Nombre;
    public string Phone => Telefono;
    public decimal CreditLimit => LimiteCredito;
    public decimal Debt => Deuda;
    /// <summary>Remaining credit headroom = LimiteCredito - Deuda. Always >= 0.</summary>
    public decimal AvailableCredit => Math.Max(0m, LimiteCredito - Deuda);
    public bool IsWholesale => Name.Contains("Mayorista", StringComparison.OrdinalIgnoreCase);
}

public record CustomerPaymentDto(
    string Id,
    string CustomerId,
    string Date,
    decimal Amount,
    string? PaymentMethod = "Efectivo",
    string? Reference = null
)
{
    public CustomerPaymentDto(string Id, string CustomerId, string Date, double Amount,
        string? PaymentMethod = "Efectivo", string? Reference = null)
        : this(Id, CustomerId, Date, Convert.ToDecimal(Amount), PaymentMethod, Reference) { }

    public string Method => PaymentMethod ?? "Efectivo";
    public string Notes => Reference ?? "";
};

public record FiscalClientDto(
    string Id,
    string CustomerId,
    string Rfc,
    string RazonSocial,
    string RegimenFiscal,
    string CodigoPostal,
    string UsoCfdi
);

public record PromotionDto(
    string Id,
    string Name,
    decimal DiscountValue,
    bool IsActive,
    Ticketfy.Core.Enums.PromotionType StrategyType = Ticketfy.Core.Enums.PromotionType.PercentageDiscount,
    string? TargetProductId = null,
    string TargetCategory = "",
    double MinQuantity = 1.0,
    double FreeQuantity = 0.0
);

public record UserDto(string Id, string Username, string FullName, string Role, bool IsActive)
{
    public string Nombre => FullName;
    public string Rol => Role;
    public string Estatus => IsActive ? "ACTIVO" : "INACTIVO";
}

public record ShiftDto(
    string Id,
    string StartTime,
    string? EndTime,
    decimal OpeningBalance,
    decimal TotalCashSales,
    decimal TotalCreditSales,
    decimal ExpectedBalance,
    decimal? ActualBalance,
    decimal? Diff,
    int IsOpen
);

public record ParkedOrderDto(string Id, DateTime CreatedAt, string CustomerName, string CartJson, decimal Total);

public partial class CartItemDto : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string ProductId => Id;
    public string Name { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal OriginalUnitPrice { get; set; }
    public decimal Cost { get; set; }
    public string Unit { get; set; } = "Pza";
    public string Category { get; set; } = "General";
    public string SatProductCode { get; set; } = "01010101";
    public string SatUnitCode { get; set; } = "H87";
    public double PointsRewarded { get; set; } = 0.0;

    private double _quantity = 1.0;
    public double Quantity
    {
        get => _quantity;
        private set
        {
            if (SetProperty(ref _quantity, value))
            {
                OnPropertyChanged(nameof(TotalPrice));
                OnPropertyChanged(nameof(FinalUnitPrice));
                OnPropertyChanged(nameof(IsFractional));
                OnPropertyChanged(nameof(IsNotFractional));
            }
        }
    }

    public (bool Success, string ErrorMessage) IncreaseQuantity(double amountToAdd, double absoluteDbStock)
    {
        if (Quantity + amountToAdd > absoluteDbStock)
        {
            return (false, $"Stock físico insuficiente. Límite: {absoluteDbStock}");
        }

        Quantity += amountToAdd;
        return (true, string.Empty);
    }

    public void DecreaseQuantity(double amountToSubtract)
    {
        if (Quantity - amountToSubtract >= 1)
        {
            Quantity -= amountToSubtract;
        }
    }

    public void OverrideQuantity(double newQuantity, double absoluteDbStock)
    {
        if (newQuantity <= absoluteDbStock)
        {
            Quantity = newQuantity;
        }
    }

    public bool IsFractional => Quantity % 1.0 != 0.0;
    public bool IsNotFractional => !IsFractional;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPrice))]
    [NotifyPropertyChangedFor(nameof(FinalUnitPrice))]
    private decimal _appliedDiscountAmount;

    [ObservableProperty]
    private string? _appliedPromotionId;

    [ObservableProperty]
    private string _promotionDescription = string.Empty;

    public decimal FinalUnitPrice => Math.Max(0m, OriginalUnitPrice - (AppliedDiscountAmount / Convert.ToDecimal(Math.Max(1.0, Quantity))));

    public decimal TotalPrice => GetLineTotal();

    public decimal GetLineTotal()
    {
        return Math.Max(0m, (OriginalUnitPrice * Convert.ToDecimal(Quantity)) - AppliedDiscountAmount);
    }

    public CartItemDto() { }

    public CartItemDto(string id, string name, decimal unitPrice, double quantity = 1.0, string unit = "Pza", double pointsRewarded = 0.0)
    {
        Id = id;
        Name = name;
        UnitPrice = unitPrice;
        OriginalUnitPrice = unitPrice;
        Quantity = quantity;
        Unit = unit;
        PointsRewarded = pointsRewarded;
    }
}

public record TicketItemDto(string ProductId, string Name, decimal UnitPrice, decimal Cost, double Quantity, string Unit, string Category, decimal Discount, decimal TotalPrice);

[method: JsonConstructor]
public record SaleItemSnapshotDto(
    string ProductId,
    string Name,
    decimal UnitPrice,
    decimal Cost,
    double Quantity,
    string Unit,
    string Category,
    decimal Discount,
    decimal TotalPrice,
    decimal OriginalUnitPrice = 0m,
    decimal AppliedDiscountAmount = 0m,
    string? AppliedPromotionId = null,
    string SatProductCode = "01010101",
    string SatUnitCode = "H87",
    [property: JsonPropertyName("proratedGlobalDiscountAmount")] decimal ProratedGlobalDiscountAmount = 0m,
    [property: JsonPropertyName("taxAmount")] decimal TaxAmount = 0m,
    [property: JsonPropertyName("returnedQuantity")] double ReturnedQuantity = 0.0,
    [property: JsonPropertyName("pointsRewarded")] double PointsRewarded = 0.0
)
{
    public SaleItemSnapshotDto(string ProductId, string Name, double UnitPrice, double Cost,
        double Quantity, string Unit, string Category, double Discount, double TotalPrice,
        double OriginalUnitPrice = 0d, double AppliedDiscountAmount = 0d,
        string? AppliedPromotionId = null, string SatProductCode = "01010101",
        string SatUnitCode = "H87", double ProratedGlobalDiscountAmount = 0d,
        double TaxAmount = 0d, double ReturnedQuantity = 0d, double PointsRewarded = 0d)
        : this(ProductId, Name, Convert.ToDecimal(UnitPrice), Convert.ToDecimal(Cost),
            Quantity, Unit, Category, Convert.ToDecimal(Discount), Convert.ToDecimal(TotalPrice),
            Convert.ToDecimal(OriginalUnitPrice), Convert.ToDecimal(AppliedDiscountAmount),
            AppliedPromotionId, SatProductCode, SatUnitCode,
            Convert.ToDecimal(ProratedGlobalDiscountAmount), Convert.ToDecimal(TaxAmount),
            ReturnedQuantity, PointsRewarded)
    { }

    public string Id => ProductId;
    public double AvailableForReturn => Math.Max(0.0, Quantity - ReturnedQuantity);
}

[method: JsonConstructor]
public record SaleDto(
    string Id,
    string Date,
    List<SaleItemSnapshotDto> Items,
    decimal Total,
    decimal TotalCost,
    decimal Profit,
    decimal PaidAmount,
    decimal ChangeAmount,
    string PaymentMethod,
    string? CustomerId,
    bool IsCredit,
    bool IsCancelled,
    string? CancelledAt,
    string EstadoFiscal = "PENDIENTE",
    string? UuidSat = null,
    string? SerieFolio = null,
    SaleStatus Status = SaleStatus.Completed,
    string? InvoiceId = null,
    string? InvoiceStatus = null,
    string? CashierUserId = null,
    string? CashierName = null,
    decimal CashAmount = 0m,
    decimal CardAmount = 0m,
    string? OperationId = null,
    string? GiftcardNumber = null,
    decimal GiftcardAmount = 0m
)
{
    public SaleDto(string Id, string Date, List<SaleItemSnapshotDto> Items,
        double Total, double TotalCost, double Profit, double PaidAmount, double ChangeAmount,
        string PaymentMethod, string? CustomerId, bool IsCredit, bool IsCancelled,
        string? CancelledAt, string EstadoFiscal = "PENDIENTE", string? UuidSat = null,
        string? SerieFolio = null, SaleStatus Status = SaleStatus.Completed,
        string? InvoiceId = null, string? InvoiceStatus = null, string? CashierUserId = null,
        string? CashierName = null, double CashAmount = 0d, double CardAmount = 0d,
        string? OperationId = null, string? GiftcardNumber = null, double GiftcardAmount = 0d)
        : this(Id, Date, Items, Convert.ToDecimal(Total), Convert.ToDecimal(TotalCost),
            Convert.ToDecimal(Profit), Convert.ToDecimal(PaidAmount), Convert.ToDecimal(ChangeAmount),
            PaymentMethod, CustomerId, IsCredit, IsCancelled, CancelledAt, EstadoFiscal, UuidSat,
            SerieFolio, Status, InvoiceId, InvoiceStatus, CashierUserId, CashierName,
            Convert.ToDecimal(CashAmount), Convert.ToDecimal(CardAmount), OperationId,
            GiftcardNumber, Convert.ToDecimal(GiftcardAmount))
    { }

    public string LocalDateDisplay => DateTimeExtensions.ToLocalDisplayString(Date);

    public string PaymentStatusDisplay
    {
        get
        {
            if (IsCancelled) return "CANCELADO";
            if (Items != null && System.Linq.Enumerable.Any(Items, i => i.ReturnedQuantity > 0))
            {
                bool allReturned = System.Linq.Enumerable.All(Items, i => i.ReturnedQuantity >= i.Quantity);
                return allReturned ? "DEVUELTO TOTAL" : "DEVOLUCIÓN PARCIAL";
            }
            return IsCredit ? "CRÉDITO (PENDIENTE)" : "COBRADO";
        }
    }

    public string PaymentStatusColor
    {
        get
        {
            if (IsCancelled) return "#EF4444"; // Red
            if (Items != null && System.Linq.Enumerable.Any(Items, i => i.ReturnedQuantity > 0)) return "#F59E0B"; // Amber
            return IsCredit ? "#3B82F6" : "#10B981"; // Blue / Green
        }
    }
}

public record SupplierDto(
    string Id,
    string Name,
    string Rfc,
    string Phone,
    string Email,
    string Address,
    string ContactPerson,
    bool IsActive = true
);

public record PurchaseItemDto(
    string Id,
    string PurchaseId,
    string ProductId,
    string ProductName,
    decimal UnitPrice,
    double Quantity,
    decimal TotalPrice
)
{
    public PurchaseItemDto(string Id, string PurchaseId, string ProductId, string ProductName,
        double UnitPrice, double Quantity, double TotalPrice)
        : this(Id, PurchaseId, ProductId, ProductName, Convert.ToDecimal(UnitPrice), Quantity,
            Convert.ToDecimal(TotalPrice))
    { }
}

public record PurchaseDto(
    string Id,
    string SupplierId,
    string SupplierName,
    string InvoiceNumber,
    string Date,
    decimal TotalCost,
    string Notes,
    List<PurchaseItemDto> Items
);

public record ExpenseDto(
    string Id,
    string Category,
    decimal Amount,
    string Date,
    string Description,
    string PaymentMethod,
    string RegisteredByUser
)
{
    public ExpenseDto(string Id, string Category, double Amount, string Date,
        string Description, string PaymentMethod, string RegisteredByUser)
        : this(Id, Category, Convert.ToDecimal(Amount), Date, Description, PaymentMethod, RegisteredByUser) { }
}

public record FinancialSummaryDto(
    decimal TotalRevenue,
    decimal TotalCostOfGoodsSold,
    decimal GrossProfit,
    decimal TotalExpenses,
    decimal NetProfit,
    decimal CashRevenue = 0m,
    decimal CashExpenses = 0m,
    decimal CardRevenue = 0m
);

public record ItemKitItemDto(
    string Id,
    string ItemKitId,
    string ProductId,
    string ProductName,
    double Quantity
);

public record ItemKitDto(
    string Id,
    string KitBarcode,
    string Name,
    decimal Price,
    string Description,
    List<ItemKitItemDto> Items
);

public record ShiftNoteDto(
    string Id,
    string CashierName,
    string CreatedAt,
    string NoteText,
    string Category,
    bool IsResolved
);

public record CashierPerformanceDto(
    string CashierName,
    int TicketCount,
    decimal TotalRevenue,
    decimal AverageTicketValue,
    decimal CommissionRatePct,
    decimal EarnedCommissionAmount
);
