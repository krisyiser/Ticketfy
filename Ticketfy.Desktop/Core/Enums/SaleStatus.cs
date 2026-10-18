namespace Ticketfy.Core.Enums;

public enum SaleStatus
{
    Completed = 0,
    Canceled = 1,
    Refunded = 2,
    Pending = 3,
    Failed = 4,
    PartiallyRefunded = 5
}

public enum PaymentStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Reversed = 3,
    RequiresReconciliation = 4
}

public enum ReturnStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}

public enum CheckoutOperationState
{
    Started = 0,
    PaymentAuthorized = 1,
    Completed = 2,
    Failed = 3,
    RequiresReconciliation = 4
}
