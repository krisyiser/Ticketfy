using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class IndexCheckoutRecoveryDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_checkout_operations_RegisterId_state_created_at_utc_operation_id",
                table: "checkout_operations",
                columns: new[] { "RegisterId", "state", "created_at_utc", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_operations_ShiftId_state",
                table: "checkout_operations",
                columns: new[] { "ShiftId", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_checkout_operations_RegisterId_state_created_at_utc_operation_id",
                table: "checkout_operations");

            migrationBuilder.DropIndex(
                name: "IX_checkout_operations_ShiftId_state",
                table: "checkout_operations");
        }
    }
}
