using System;
using System.Collections.Generic;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.Models;

public record SaleCreationDto(
    string? CustomerId,
    List<CartItemDto> Items,
    decimal Total,
    decimal CreditAmount,
    PaymentMethod PaymentMethod,
    string? GiftcardNumber = null,
    decimal GiftcardAmount = 0m,
    string? OperationId = null
);

public class SaleResultModel
{
    public bool IsSuccess { get; set; }
    public string SaleId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}
