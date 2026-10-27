using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class IndexPredictiveReadPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_CreatedAtUtc_ProductId",
                table: "inventory_movements",
                columns: new[] { "CreatedAtUtc", "ProductId" },
                filter: "\"SaleLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_co_ocurrencia_producto_b_producto_a",
                table: "co_ocurrencia",
                columns: new[] { "producto_b", "producto_a" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inventory_movements_CreatedAtUtc_ProductId",
                table: "inventory_movements");

            migrationBuilder.DropIndex(
                name: "IX_co_ocurrencia_producto_b_producto_a",
                table: "co_ocurrencia");
        }
    }
}
