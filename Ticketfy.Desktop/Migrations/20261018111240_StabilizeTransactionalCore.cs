using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class StabilizeTransactionalCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SqliteDecimalConverter stores money as integer cents. Scale legacy REAL
            // values before EF rebuilds the affected tables as INTEGER columns.
            migrationBuilder.Sql("UPDATE shifts SET totalCreditSales=ROUND(totalCreditSales*100), totalCashSales=ROUND(totalCashSales*100), openingBalance=ROUND(openingBalance*100), expectedBalance=ROUND(expectedBalance*100), diff=CASE WHEN diff IS NULL THEN NULL ELSE ROUND(diff*100) END, actualBalance=CASE WHEN actualBalance IS NULL THEN NULL ELSE ROUND(actualBalance*100) END;");
            migrationBuilder.Sql("UPDATE shift_movements SET amount=ROUND(amount*100);");
            migrationBuilder.Sql("UPDATE sales SET totalCost=ROUND(totalCost*100), total=ROUND(total*100), profit=ROUND(profit*100), paidAmount=ROUND(paidAmount*100), changeAmount=ROUND(changeAmount*100), cardAmount=ROUND(cardAmount*100), cashAmount=ROUND(cashAmount*100);");
            migrationBuilder.Sql("UPDATE returns SET total_refunded=ROUND(total_refunded*100), cogs_reversed=ROUND(cogs_reversed*100);");
            migrationBuilder.Sql("UPDATE Purchases SET TotalCost=ROUND(TotalCost*100);");
            migrationBuilder.Sql("UPDATE PurchaseItems SET UnitPrice=ROUND(UnitPrice*100), TotalPrice=ROUND(TotalPrice*100);");
            migrationBuilder.Sql("UPDATE promotions SET discountValue=ROUND(discountValue*100);");
            migrationBuilder.Sql("UPDATE products SET wholesalePrice=ROUND(wholesalePrice*100), price=ROUND(price*100), cost=ROUND(cost*100);");
            migrationBuilder.Sql("UPDATE item_kits SET price=ROUND(price*100);");
            migrationBuilder.Sql("UPDATE giftcards SET balance=ROUND(balance*100);");
            migrationBuilder.Sql("UPDATE Expenses SET Amount=ROUND(Amount*100);");
            migrationBuilder.Sql("UPDATE customers SET debt=ROUND(debt*100), credit_limit=ROUND(credit_limit*100);");
            migrationBuilder.Sql("UPDATE customer_payments SET amount=ROUND(amount*100);");
            migrationBuilder.Sql("UPDATE cashups SET theoretical_cash=ROUND(theoretical_cash*100), open_cash_amount=ROUND(open_cash_amount*100), difference=ROUND(difference*100), closed_cash_amount=ROUND(closed_cash_amount*100);");

            migrationBuilder.AlterColumn<long>(
                name: "totalCreditSales",
                table: "shifts",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "totalCashSales",
                table: "shifts",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "openingBalance",
                table: "shifts",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "expectedBalance",
                table: "shifts",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "diff",
                table: "shifts",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "actualBalance",
                table: "shifts",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "amount",
                table: "shift_movements",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "totalCost",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "total",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "profit",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "paidAmount",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "changeAmount",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddColumn<string>(
                name: "committed_at_utc",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "giftcard_amount",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "giftcard_number",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "operation_id",
                table: "sales",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<long>(
                name: "total_refunded",
                table: "returns",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "cogs_reversed",
                table: "returns",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddColumn<string>(
                name: "operation_id",
                table: "returns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "returns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<long>(
                name: "TotalCost",
                table: "Purchases",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "UnitPrice",
                table: "PurchaseItems",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "TotalPrice",
                table: "PurchaseItems",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "discountValue",
                table: "promotions",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "wholesalePrice",
                table: "products",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "price",
                table: "products",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "cost",
                table: "products",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddColumn<long>(
                name: "stock_version",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "price",
                table: "item_kits",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "balance",
                table: "giftcards",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "Amount",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "debt",
                table: "customers",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "credit_limit",
                table: "customers",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "amount",
                table: "customer_payments",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddColumn<string>(
                name: "operation_id",
                table: "customer_payments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "customer_payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<long>(
                name: "theoretical_cash",
                table: "cashups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "open_cash_amount",
                table: "cashups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "difference",
                table: "cashups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<long>(
                name: "closed_cash_amount",
                table: "cashups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.CreateTable(
                name: "checkout_operations",
                columns: table => new
                {
                    operation_id = table.Column<string>(type: "TEXT", nullable: false),
                    sale_id = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    payment_method = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<long>(type: "INTEGER", nullable: false),
                    payment_reference = table.Column<string>(type: "TEXT", nullable: true),
                    sale_payload = table.Column<string>(type: "TEXT", nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_operations", x => x.operation_id);
                });

            migrationBuilder.CreateTable(
                name: "transaction_journal",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<long>(type: "INTEGER", nullable: false),
                    details = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transaction_journal", x => x.id);
                });

            // Existing rows predate idempotency keys. Give each one a stable unique key
            // before unique indexes are created.
            migrationBuilder.Sql("UPDATE sales SET operation_id=lower(hex(randomblob(16))) WHERE operation_id='';");
            migrationBuilder.Sql("UPDATE returns SET operation_id=lower(hex(randomblob(16))) WHERE operation_id='';");
            migrationBuilder.Sql("UPDATE customer_payments SET operation_id=lower(hex(randomblob(16))) WHERE operation_id='';");

            migrationBuilder.CreateIndex(
                name: "IX_sales_operation_id",
                table: "sales",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_returns_operation_id",
                table: "returns",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_payments_operation_id",
                table: "customer_payments",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_operation_id_event_type",
                table: "transaction_journal",
                columns: new[] { "operation_id", "event_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkout_operations");

            migrationBuilder.DropTable(
                name: "transaction_journal");

            migrationBuilder.DropIndex(
                name: "IX_sales_operation_id",
                table: "sales");

            migrationBuilder.DropIndex(
                name: "IX_returns_operation_id",
                table: "returns");

            migrationBuilder.DropIndex(
                name: "IX_customer_payments_operation_id",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "committed_at_utc",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "giftcard_amount",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "giftcard_number",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "returns");

            migrationBuilder.DropColumn(
                name: "status",
                table: "returns");

            migrationBuilder.DropColumn(
                name: "stock_version",
                table: "products");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "customer_payments");

            migrationBuilder.DropColumn(
                name: "status",
                table: "customer_payments");

            migrationBuilder.AlterColumn<double>(
                name: "totalCreditSales",
                table: "shifts",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "totalCashSales",
                table: "shifts",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "openingBalance",
                table: "shifts",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "expectedBalance",
                table: "shifts",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "diff",
                table: "shifts",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "actualBalance",
                table: "shifts",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "amount",
                table: "shift_movements",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "totalCost",
                table: "sales",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "total",
                table: "sales",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "profit",
                table: "sales",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "paidAmount",
                table: "sales",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "changeAmount",
                table: "sales",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "total_refunded",
                table: "returns",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "cogs_reversed",
                table: "returns",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "TotalCost",
                table: "Purchases",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "UnitPrice",
                table: "PurchaseItems",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "TotalPrice",
                table: "PurchaseItems",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "discountValue",
                table: "promotions",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "wholesalePrice",
                table: "products",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "price",
                table: "products",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "cost",
                table: "products",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "price",
                table: "item_kits",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "balance",
                table: "giftcards",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "Amount",
                table: "Expenses",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "debt",
                table: "customers",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "credit_limit",
                table: "customers",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "amount",
                table: "customer_payments",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "theoretical_cash",
                table: "cashups",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "open_cash_amount",
                table: "cashups",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "difference",
                table: "cashups",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<double>(
                name: "closed_cash_amount",
                table: "cashups",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");
        }
    }
}
