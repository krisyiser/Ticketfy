using System.Collections.Generic;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.Messages;

public record CartStateSnapshotMessage(
    IReadOnlyList<CartItemDto> Items,
    decimal Subtotal,
    decimal TotalDiscount,
    decimal GrandTotal,
    string LastAddedProductName
);

public record CustomerDisplayIdleStateMessage(bool IsIdle);

public record FocusSearchMessage();

public record UserDeletedMessage(string UserId);

public record CashupSavedMessage();
