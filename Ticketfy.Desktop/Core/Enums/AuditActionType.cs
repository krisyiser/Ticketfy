namespace Ticketfy.Core.Enums;

public enum AuditActionType
{
    CartItemRemoved = 0,
    CartCleared = 1,
    ParkedOrderCancelled = 2,
    PriceOverride = 3,
    ManualDiscountExceeded = 4,
    InventoryStockAdjustment = 5,
    ShiftDrawerOpenedManually = 6,
    UserLoginFailed = 7,
    UserCreated = 8,
    UserUpdated = 9,
    UserDeleted = 10,
    RolePermissionsChanged = 11,
    SecuritySettingsChanged = 12,
    LicenseStateChanged = 13,
    AuditIntegrityFailure = 14
}
