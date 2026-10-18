using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class CommercialLedgersAndRegisterOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "transaction_journal",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "transaction_journal",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "transaction_journal",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "shifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseOperationId",
                table: "shifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenOperationId",
                table: "shifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "shifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "shifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "shift_movements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "shift_movements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "shift_movements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierName",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LedgerVersion",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "returns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "returns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "Purchases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DueDate",
                table: "Purchases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LedgerVersion",
                table: "Purchases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "Purchases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "Purchases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "Purchases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxStock",
                table: "products",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<long>(
                name: "TaxRate",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: 16L);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledAtUtc",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "customer_payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "customer_payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "checkout_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalIdempotencyKey",
                table: "checkout_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalPayload",
                table: "checkout_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "checkout_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "checkout_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "cashups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "cashups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "cashups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_entries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AccountKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryType = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    DueDate = table.Column<string>(type: "TEXT", nullable: true),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "command_receipts",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_receipts", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    SaleLineId = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    StockBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    StockAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitCost = table.Column<long>(type: "INTEGER", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sale_lines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SaleId = table.Column<string>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitCost = table.Column<long>(type: "INTEGER", nullable: false),
                    Total = table.Column<long>(type: "INTEGER", nullable: false),
                    CostTotal = table.Column<long>(type: "INTEGER", nullable: false),
                    Discount = table.Column<long>(type: "INTEGER", nullable: false),
                    Tax = table.Column<long>(type: "INTEGER", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    PromotionId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_lines_sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SaleId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    AccountId = table.Column<string>(type: "TEXT", nullable: true),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_payments_sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<string>(type: "TEXT", nullable: false),
                    PurchaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_returns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    PurchaseId = table.Column<string>(type: "TEXT", nullable: false),
                    PurchaseItemId = table.Column<string>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_returns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tender_confirmations",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tender_confirmations", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "return_lines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ReturnId = table.Column<string>(type: "TEXT", nullable: false),
                    SaleLineId = table.Column<string>(type: "TEXT", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    CostReversed = table.Column<long>(type: "INTEGER", nullable: false),
                    Restocked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_return_lines_returns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "returns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_lines_sale_lines_SaleLineId",
                        column: x => x.SaleLineId,
                        principalTable: "sale_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ReturnId = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_refunds_sale_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "sale_payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_settlements",
                columns: table => new
                {
                    RefundId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_settlements", x => x.RefundId);
                    table.ForeignKey(
                        name: "FK_refund_settlements_payment_refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "payment_refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shifts_OpenOperationId",
                table: "shifts",
                column: "OpenOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_RegisterId",
                table: "shifts",
                column: "RegisterId",
                unique: true,
                filter: "\"isOpen\" = 1 AND \"RegisterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_OperationId",
                table: "Purchases",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_InvoiceNumber",
                table: "Purchases",
                columns: new[] { "SupplierId", "InvoiceNumber" },
                unique: true,
                filter: "\"InvoiceNumber\" <> '' AND \"LedgerVersion\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_products_barcode",
                table: "products",
                column: "barcode",
                unique: true,
                filter: "\"barcode\" IS NOT NULL AND \"barcode\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_giftcards_card_number",
                table: "giftcards",
                column: "card_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_OperationId",
                table: "Expenses",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_customer_code",
                table: "customers",
                column: "customer_code",
                unique: true,
                filter: "\"customer_code\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_cashups_OperationId",
                table: "cashups",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_AccountKind_AccountId_CreatedAtUtc",
                table: "account_entries",
                columns: new[] { "AccountKind", "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_OperationId_AccountKind_AccountId_DocumentId_EntryType",
                table: "account_entries",
                columns: new[] { "OperationId", "AccountKind", "AccountId", "DocumentId", "EntryType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId_CreatedAtUtc",
                table: "inventory_movements",
                columns: new[] { "ProductId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_OperationId_PaymentId",
                table: "payment_refunds",
                columns: new[] { "OperationId", "PaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_PaymentId",
                table: "payment_refunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_refund_settlements_ExternalReference",
                table: "refund_settlements",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refund_settlements_OperationId",
                table: "refund_settlements",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_return_lines_ReturnId",
                table: "return_lines",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_return_lines_SaleLineId",
                table: "return_lines",
                column: "SaleLineId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_lines_SaleId_LineNumber",
                table: "sale_lines",
                columns: new[] { "SaleId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_payments_ExternalReference",
                table: "sale_payments",
                column: "ExternalReference",
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_payments_OperationId_Kind",
                table: "sale_payments",
                columns: new[] { "OperationId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_payments_SaleId",
                table: "sale_payments",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_OperationId",
                table: "supplier_payments",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_returns_OperationId",
                table: "supplier_returns",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tender_confirmations_Kind_ExternalReference_Purpose",
                table: "tender_confirmations",
                columns: new[] { "Kind", "ExternalReference", "Purpose" },
                unique: true);
            InstallLedgerGuards(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new System.NotSupportedException("No se admite degradar una base con ledgers. Restaure el respaldo previo verificado.");
    }
}
