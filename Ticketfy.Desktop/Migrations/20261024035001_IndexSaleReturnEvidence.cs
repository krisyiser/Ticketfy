using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class IndexSaleReturnEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_ReturnId",
                table: "payment_refunds",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_DocumentId_Reason",
                table: "inventory_movements",
                columns: new[] { "DocumentId", "Reason" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_SaleLineId_Reason",
                table: "inventory_movements",
                columns: new[] { "SaleLineId", "Reason" });

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_DocumentId_EntryType",
                table: "account_entries",
                columns: new[] { "DocumentId", "EntryType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_refunds_ReturnId",
                table: "payment_refunds");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_DocumentId_Reason",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_SaleLineId_Reason",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_account_entries_DocumentId_EntryType",
                table: "account_entries");
        }
    }
}
