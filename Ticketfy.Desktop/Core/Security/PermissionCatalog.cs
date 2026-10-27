using System;
using System.Collections.Generic;
using System.Linq;

namespace Ticketfy.Core.Security;

/// <summary>Single source of truth for every authorization decision.</summary>
public static class PermissionCatalog
{
    public const string PosCheckout = "pos.checkout";
    public const string PosDiscount = "pos.discount";
    public const string PosModifyPrice = "pos.modify_price";
    public const string PosCancelSale = "pos.cancel_sale";
    public const string PosRefund = "pos.refund";
    public const string PosApplyPoints = "pos.apply_points";
    public const string PosCreditSale = "pos.credit_sale";
    public const string CashOpen = "cash.open";
    public const string CashClose = "cash.close";
    public const string CashInOut = "cash.in_out";
    public const string CashHistory = "cash.history";
    public const string InventoryView = "inventory.view";
    public const string InventoryCreate = "inventory.create";
    public const string InventoryEdit = "inventory.edit";
    public const string InventoryDelete = "inventory.delete";
    public const string InventoryStockAdjust = "inventory.stock_adjust";
    public const string CustomersView = "customers.view";
    public const string CustomersCreateEdit = "customers.create_edit";
    public const string CustomersDelete = "customers.delete";
    public const string CustomersCreditLimit = "customers.credit_limit";
    public const string QuotesManage = "quotes.manage";
    public const string ReportsView = "reports.view";
    public const string ReportsProfits = "reports.profits";
    public const string ReportsExport = "reports.export";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string UsersRoles = "users.roles";
    public const string SettingsBusiness = "settings.business";
    public const string SettingsHardware = "settings.hardware";
    public const string SettingsSecurity = "settings.security";
    public const string PurchasesManage = "purchases.manage";
    public const string SuppliersManage = "suppliers.manage";
    public const string ExpensesManage = "expenses.manage";
    public const string PromotionsManage = "promotions.manage";
    public const string WalletManage = "wallet.manage";
    public const string PaymentsReconcile = "payments.reconcile";
    public const string CheckoutRecover = "pos.recover_checkout";
    public const string AttendanceManage = "attendance.manage";
    public const string ShiftNotesManage = "cash.notes_manage";
    public const string AccountCreditSettle = "accounts.settle_credit";
    public const string HistoryReconcile = "history.reconcile";
    public static IReadOnlySet<string> AdministratorRecovery { get; } =
        new HashSet<string>([UsersManage, UsersRoles, SettingsBusiness, SettingsSecurity], StringComparer.Ordinal);

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        PosCheckout, PosDiscount, PosModifyPrice, PosCancelSale, PosRefund, PosApplyPoints, PosCreditSale,
        CashOpen, CashClose, CashInOut, CashHistory,
        InventoryView, InventoryCreate, InventoryEdit, InventoryDelete, InventoryStockAdjust,
        CustomersView, CustomersCreateEdit, CustomersDelete, CustomersCreditLimit,
        QuotesManage, ReportsView, ReportsProfits, ReportsExport,
        UsersView, UsersManage, UsersRoles, SettingsBusiness, SettingsHardware, SettingsSecurity,
        PurchasesManage, SuppliersManage, ExpensesManage, PromotionsManage, WalletManage, PaymentsReconcile, CheckoutRecover, HistoryReconcile,
        AttendanceManage, ShiftNotesManage, AccountCreditSettle
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Grants =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADMINISTRADOR"] = new HashSet<string>(All, StringComparer.Ordinal),
            ["GERENTE"] = SetExcept(InventoryDelete, CustomersDelete, UsersManage, UsersRoles, SettingsBusiness, SettingsSecurity, HistoryReconcile),
            ["SUPERVISOR"] = Set(PosCheckout, PosDiscount, PosCancelSale, PosRefund, PosApplyPoints,
                CashOpen, CashClose, CashInOut, CashHistory, InventoryView, CustomersView,
                CustomersCreateEdit, QuotesManage, ReportsView, UsersView, CheckoutRecover, AttendanceManage, ShiftNotesManage),
            ["VENDEDOR"] = Set(PosCheckout, PosApplyPoints, InventoryView, CustomersView, CustomersCreateEdit, QuotesManage),
            ["CAJERO"] = Set(PosCheckout, PosApplyPoints, CashOpen, CashClose, CashInOut, InventoryView, CustomersView)
        };

    public static bool IsKnown(string permissionKey) => All.Contains(permissionKey);

    public static bool IsGrantedByDefault(string normalizedRole, string permissionKey) =>
        IsKnown(permissionKey) && Grants.TryGetValue(normalizedRole, out var permissions) && permissions.Contains(permissionKey);

    public static Dictionary<string, bool> GetDefaultPermissions(string normalizedRole) =>
        All.ToDictionary(key => key, key => IsGrantedByDefault(normalizedRole, key), StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
    private static IReadOnlySet<string> SetExcept(params string[] excluded) =>
        new HashSet<string>(All.Except(excluded, StringComparer.Ordinal), StringComparer.Ordinal);
}
