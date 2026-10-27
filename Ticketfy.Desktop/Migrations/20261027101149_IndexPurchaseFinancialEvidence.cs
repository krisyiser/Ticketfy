using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class IndexPurchaseFinancialEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_supplier_returns_PurchaseId",
                table: "supplier_returns",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_returns_PurchaseItemId",
                table: "supplier_returns",
                column: "PurchaseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_PurchaseId",
                table: "supplier_payments",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseItems_PurchaseId_ProductId_UnitPrice",
                table: "PurchaseItems",
                columns: new[] { "PurchaseId", "ProductId", "UnitPrice" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_OperationId",
                table: "inventory_movements",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_AccountKind_DocumentId_PostingSequence",
                table: "account_entries",
                columns: new[] { "AccountKind", "DocumentId", "PostingSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_account_credit_settlements_AccountKind_SourceDocumentId",
                table: "account_credit_settlements",
                columns: new[] { "AccountKind", "SourceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_account_credit_settlements_AccountKind_TargetDocumentId",
                table: "account_credit_settlements",
                columns: new[] { "AccountKind", "TargetDocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_returns_PurchaseId",
                table: "supplier_returns");

            migrationBuilder.DropIndex(
                name: "IX_supplier_returns_PurchaseItemId",
                table: "supplier_returns");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payments_PurchaseId",
                table: "supplier_payments");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseItems_PurchaseId_ProductId_UnitPrice",
                table: "PurchaseItems");

            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_OperationId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_account_entries_AccountKind_DocumentId_PostingSequence",
                table: "account_entries");

            migrationBuilder.DropIndex(
                name: "IX_account_credit_settlements_AccountKind_SourceDocumentId",
                table: "account_credit_settlements");

            migrationBuilder.DropIndex(
                name: "IX_account_credit_settlements_AccountKind_TargetDocumentId",
                table: "account_credit_settlements");
        }
    }
}
