using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class IndexRefundRecoveryQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_shift_movements_OperationId",
                table: "shift_movements",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_State_Id",
                table: "payment_refunds",
                columns: new[] { "State", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_refund_requests_RegisterId_OperationId",
                table: "credit_refund_requests",
                columns: new[] { "RegisterId", "OperationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shift_movements_OperationId",
                table: "shift_movements");

            migrationBuilder.DropIndex(
                name: "IX_payment_refunds_State_Id",
                table: "payment_refunds");

            migrationBuilder.DropIndex(
                name: "IX_credit_refund_requests_RegisterId_OperationId",
                table: "credit_refund_requests");
        }
    }
}
