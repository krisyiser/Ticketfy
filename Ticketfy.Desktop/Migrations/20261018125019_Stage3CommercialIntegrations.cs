using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class Stage3CommercialIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fiscal_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    sale_id = table.Column<string>(type: "TEXT", nullable: false),
                    operation_type = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    provider_document_id = table.Column<string>(type: "TEXT", nullable: true),
                    provider_status = table.Column<string>(type: "TEXT", nullable: true),
                    uuid_sat = table.Column<string>(type: "TEXT", nullable: true),
                    cancellation_motive = table.Column<string>(type: "TEXT", nullable: true),
                    replacement_uuid = table.Column<string>(type: "TEXT", nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    lease_until_utc = table.Column<string>(type: "TEXT", nullable: true),
                    last_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    last_error_message = table.Column<string>(type: "TEXT", nullable: true),
                    xml_path = table.Column<string>(type: "TEXT", nullable: true),
                    xml_sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    pdf_path = table.Column<string>(type: "TEXT", nullable: true),
                    pdf_sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_operations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_webhook_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", nullable: false),
                    payload_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    processed = table.Column<bool>(type: "INTEGER", nullable: false),
                    received_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    processed_at_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_terminal_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    external_reference = table.Column<string>(type: "TEXT", nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    terminal_id = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    provider_status = table.Column<string>(type: "TEXT", nullable: true),
                    provider_order_id = table.Column<string>(type: "TEXT", nullable: true),
                    provider_payment_id = table.Column<string>(type: "TEXT", nullable: true),
                    authorization_code = table.Column<string>(type: "TEXT", nullable: true),
                    card_brand = table.Column<string>(type: "TEXT", nullable: true),
                    last_four_digits = table.Column<string>(type: "TEXT", nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    last_error_message = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_terminal_operations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_operations_idempotency_key",
                table: "fiscal_operations",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_operations_status_next_attempt_at_utc",
                table: "fiscal_operations",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_webhook_events_provider_event_id",
                table: "integration_webhook_events",
                columns: new[] { "provider", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_terminal_operations_external_reference",
                table: "payment_terminal_operations",
                column: "external_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_terminal_operations_idempotency_key",
                table: "payment_terminal_operations",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_terminal_operations_provider_order_id",
                table: "payment_terminal_operations",
                column: "provider_order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fiscal_operations");

            migrationBuilder.DropTable(
                name: "integration_webhook_events");

            migrationBuilder.DropTable(
                name: "payment_terminal_operations");
        }
    }
}
