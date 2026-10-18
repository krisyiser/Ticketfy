using Microsoft.EntityFrameworkCore.Migrations;

namespace Ticketfy.Desktop.Migrations;

public partial class CommercialLedgersAndRegisterOwnership
{
    private static void InstallLedgerGuards(MigrationBuilder migrationBuilder)
    {
        // Baselines preserve known balances. They do not invent historical transactions or human attribution.
        migrationBuilder.Sql("""
            INSERT INTO inventory_movements
              (Id, ProductId, OperationId, DocumentId, Reason, Quantity, StockBefore, StockAfter, UnitCost, RegisterId, CashierUserId, CreatedAtUtc)
            SELECT 'OPEN:' || id, id, 'MIGRATION:STAGE6', 'OPENING:' || id, 'MIGRATION_OPENING',
              CAST(ROUND(stock * 1000) AS INTEGER), 0, CAST(ROUND(stock * 1000) AS INTEGER), cost,
              'system:migration', 'system:migration:stage6', strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM products WHERE is_kit = 0;
            INSERT INTO account_entries
              (Id, AccountKind, AccountId, OperationId, DocumentId, EntryType, Amount, RegisterId, CashierUserId, CreatedAtUtc)
            SELECT 'CUSTOMER:' || id, 0, id, 'MIGRATION:STAGE6', 'OPENING:' || id, 'MIGRATION_OPENING', debt,
              'system:migration', 'system:migration:stage6', strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM customers WHERE debt <> 0;
            INSERT INTO account_entries
              (Id, AccountKind, AccountId, OperationId, DocumentId, EntryType, Amount, RegisterId, CashierUserId, CreatedAtUtc)
            SELECT 'LOYALTY:' || id, 3, id, 'MIGRATION:STAGE6', 'OPENING:' || id, 'MIGRATION_OPENING', CAST(ROUND(puntos_saldo * 100) AS INTEGER),
              'system:migration', 'system:migration:stage6', strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM customers WHERE puntos_saldo <> 0;
            INSERT INTO account_entries
              (Id, AccountKind, AccountId, OperationId, DocumentId, EntryType, Amount, RegisterId, CashierUserId, CreatedAtUtc)
            SELECT 'WALLET:' || id, 2, card_number, 'MIGRATION:STAGE6', 'OPENING:' || id, 'MIGRATION_OPENING', balance,
              'system:migration', 'system:migration:stage6', strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM giftcards WHERE balance <> 0;
            UPDATE promotions SET isActive = 0 WHERE id IN (SELECT id FROM products WHERE is_kit = 1);
            """);
        string[] tables = ["sale_payments", "sale_lines", "return_lines", "payment_refunds",
            "inventory_movements", "account_entries", "supplier_payments", "supplier_returns", "command_receipts",
            "tender_confirmations", "refund_settlements", "transaction_journal", "shift_movements", "cashups", "returns",
            "customer_payments", "PurchaseItems"];
        foreach (string table in tables)
            foreach (string action in new[] { "UPDATE", "DELETE" })
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_{action} BEFORE {action} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Posted records are immutable; use a compensating operation'); END;");
        migrationBuilder.Sql("""
            CREATE TRIGGER immutable_sale_financials BEFORE UPDATE OF
                total,totalCost,profit,paidAmount,changeAmount,paymentMethod,customerId,items,cashAmount,cardAmount,
                giftcard_amount,giftcard_number,isCredit,operation_id,ShiftId,RegisterId,CashierUserId,CashierName,date,LedgerVersion ON sales
            WHEN OLD.LedgerVersion > 0
            BEGIN SELECT RAISE(ABORT, 'Posted sale amounts and attribution are immutable'); END;
            CREATE TRIGGER immutable_sale_delete BEFORE DELETE ON sales
            BEGIN SELECT RAISE(ABORT, 'Sales cannot be deleted'); END;
            CREATE TRIGGER immutable_purchase BEFORE UPDATE ON Purchases WHEN OLD.LedgerVersion > 0
            BEGIN SELECT RAISE(ABORT, 'Purchases require a compensating document'); END;
            CREATE TRIGGER immutable_purchase_delete BEFORE DELETE ON Purchases
            BEGIN SELECT RAISE(ABORT, 'Purchases cannot be deleted'); END;
            CREATE TRIGGER immutable_expense_amount BEFORE UPDATE OF Amount,PaymentMethod,ShiftId,RegisterId,RegisteredByUser ON Expenses
            WHEN OLD.OperationId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'Expenses require cancellation'); END;
            CREATE TRIGGER immutable_expense_delete BEFORE DELETE ON Expenses
            BEGIN SELECT RAISE(ABORT, 'Expenses require cancellation'); END;
            CREATE TRIGGER sale_requires_scope BEFORE INSERT ON sales
            WHEN NEW.LedgerVersion > 0 AND (
                NEW.RegisterId IS NULL OR NEW.CashierUserId IS NULL OR NEW.ShiftId IS NULL OR
                NOT EXISTS (SELECT 1 FROM shifts WHERE id=NEW.ShiftId AND RegisterId=NEW.RegisterId
                    AND CashierUserId=NEW.CashierUserId AND isOpen=1))
            BEGIN SELECT RAISE(ABORT, 'A sale requires its open register, shift and cashier'); END;
            """);
    }
}
