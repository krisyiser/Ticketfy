using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalBalancesAndIncomingTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tender_confirmations_Kind_ExternalReference_Purpose",
                table: "tender_confirmations");

            migrationBuilder.CreateTable(
                name: "historical_balance_reconciliations",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    LedgerBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    CachedBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    Adjustment = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_balance_reconciliations", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "incoming_transfer_intents",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_transfer_intents", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "incoming_transfer_receipts",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    RefundReference = table.Column<string>(type: "TEXT", nullable: true),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_transfer_receipts", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tender_confirmations_Kind_ExternalReference",
                table: "tender_confirmations",
                columns: new[] { "Kind", "ExternalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_historical_balance_reconciliations_Scope_TargetId",
                table: "historical_balance_reconciliations",
                columns: new[] { "Scope", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_incoming_transfer_receipts_ExternalReference",
                table: "incoming_transfer_receipts",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incoming_transfer_receipts_RefundReference",
                table: "incoming_transfer_receipts",
                column: "RefundReference",
                unique: true,
                filter: "\"RefundReference\" IS NOT NULL");

            migrationBuilder.Sql("""
                INSERT INTO incoming_transfer_receipts
                    (OperationId, Purpose, DocumentId, Amount, ExternalReference, Outcome, CashierUserId, RegisterId, ShiftId, CreatedAtUtc)
                SELECT OperationId, 'CAPTURE', SaleId, Amount, ExternalReference, 0, CashierUserId, RegisterId, ShiftId, CreatedAtUtc
                FROM sale_payments WHERE Kind = 4 AND ExternalReference IS NOT NULL AND trim(ExternalReference) <> '';
                CREATE UNIQUE INDEX transfer_confirmation_reference_nocase ON tender_confirmations (Kind, ExternalReference COLLATE NOCASE);
                CREATE UNIQUE INDEX transfer_receipt_reference_nocase ON incoming_transfer_receipts (ExternalReference COLLATE NOCASE);
                CREATE UNIQUE INDEX transfer_refund_reference_nocase ON incoming_transfer_receipts (RefundReference COLLATE NOCASE) WHERE RefundReference IS NOT NULL;
                CREATE TRIGGER reconciled_historical_purchase_immutable BEFORE UPDATE ON Purchases
                WHEN EXISTS (SELECT 1 FROM historical_balance_reconciliations WHERE Scope = 1 AND TargetId = OLD.Id)
                BEGIN SELECT RAISE(ABORT, 'A reconciled historical purchase requires a compensating document'); END;
                """);
            foreach (string table in new[] { "historical_balance_reconciliations", "incoming_transfer_intents", "incoming_transfer_receipts" })
                foreach (string action in new[] { "UPDATE", "DELETE" })
                    migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_{action} BEFORE {action} ON {table} BEGIN SELECT RAISE(ABORT, 'Posted records are immutable'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se descarta evidencia financiera. Restaure el respaldo previo y concilie movimientos posteriores.");
        }
    }
}
