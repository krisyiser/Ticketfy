using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class PreserveTerminalEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "funds_observed",
                table: "payment_terminal_operations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Historical terminal labels were not validated and cannot prove absence of funds.
            migrationBuilder.AddColumn<bool>(
                name: "no_funds_confirmed",
                table: "payment_terminal_operations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "provider_evidence_hash",
                table: "payment_terminal_operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_updated_at_utc",
                table: "payment_terminal_operations",
                type: "TEXT",
                nullable: true);

            // Preserve existing evidence, without inventing a provider timestamp.
            migrationBuilder.Sql("""
                UPDATE payment_terminal_operations SET funds_observed = 1
                WHERE status = 3 OR provider_status IN ('processed', 'refunded')
                   OR EXISTS (SELECT 1 FROM sale_payments p
                              WHERE p.OperationId = payment_terminal_operations.external_reference AND p.Kind = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrading must not erase a confirmed or reconciled bank observation.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE terminal_evidence_rollback_guard (count INTEGER CHECK (count = 0));
                INSERT INTO terminal_evidence_rollback_guard
                SELECT COUNT(*) FROM payment_terminal_operations
                WHERE funds_observed = 1 OR no_funds_confirmed = 1 OR provider_evidence_hash IS NOT NULL;
                DROP TABLE terminal_evidence_rollback_guard;
                """);
            migrationBuilder.DropColumn(
                name: "funds_observed",
                table: "payment_terminal_operations");

            migrationBuilder.DropColumn(
                name: "no_funds_confirmed",
                table: "payment_terminal_operations");

            migrationBuilder.DropColumn(
                name: "provider_evidence_hash",
                table: "payment_terminal_operations");

            migrationBuilder.DropColumn(
                name: "provider_updated_at_utc",
                table: "payment_terminal_operations");
        }
    }
}
