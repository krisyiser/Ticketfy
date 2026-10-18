using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Migrations
{
    /// <inheritdoc />
    public partial class AddSatProductCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Consolidates columns historically created by startup SQL patches.
            migrationBuilder.AddColumn<string>(name: "role_string", table: "usuarios", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<double>(name: "cardAmount", table: "sales", type: "REAL", nullable: false, defaultValue: 0d);
            migrationBuilder.AddColumn<double>(name: "cashAmount", table: "sales", type: "REAL", nullable: false, defaultValue: 0d);
            migrationBuilder.AddColumn<bool>(name: "is_bulk", table: "products", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(name: "type", table: "cashups", type: "TEXT", nullable: false, defaultValue: "Final");
            migrationBuilder.AddColumn<string>(name: "cashier_name", table: "cashups", type: "TEXT", nullable: false, defaultValue: "Cajero en turno");
            migrationBuilder.AddColumn<string>(name: "cashier_role", table: "cashups", type: "TEXT", nullable: false, defaultValue: "CAJERO");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.RenameColumn(
                name: "unidad_sat",
                table: "products",
                newName: "sat_unit_code");

            migrationBuilder.RenameColumn(
                name: "clave_sat",
                table: "products",
                newName: "sat_product_code");

            migrationBuilder.AddColumn<byte[]>(
                name: "password_hash_bytes",
                table: "usuarios",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "password_salt",
                table: "usuarios",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_date",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_id",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_status",
                table: "sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "sales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "co_ocurrencia",
                columns: table => new
                {
                    producto_a = table.Column<string>(type: "TEXT", nullable: false),
                    producto_b = table.Column<string>(type: "TEXT", nullable: false),
                    frecuencia = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_co_ocurrencia", x => new { x.producto_a, x.producto_b });
                });

            migrationBuilder.CreateTable(
                name: "FolioSequences",
                columns: table => new
                {
                    DatePrefix = table.Column<string>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolioSequences", x => x.DatePrefix);
                });

            migrationBuilder.CreateTable(
                name: "InventorySnapshots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    TotalItems = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalValue = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventorySnapshotItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    CostPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    SellingPrice = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySnapshotItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventorySnapshotItems_InventorySnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "InventorySnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySnapshotItems_SnapshotId",
                table: "InventorySnapshotItems",
                column: "SnapshotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "role_string", table: "usuarios");
            migrationBuilder.DropColumn(name: "cardAmount", table: "sales");
            migrationBuilder.DropColumn(name: "cashAmount", table: "sales");
            migrationBuilder.DropColumn(name: "is_bulk", table: "products");
            migrationBuilder.DropColumn(name: "type", table: "cashups");
            migrationBuilder.DropColumn(name: "cashier_name", table: "cashups");
            migrationBuilder.DropColumn(name: "cashier_role", table: "cashups");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "co_ocurrencia");

            migrationBuilder.DropTable(
                name: "FolioSequences");

            migrationBuilder.DropTable(
                name: "InventorySnapshotItems");

            migrationBuilder.DropTable(
                name: "InventorySnapshots");

            migrationBuilder.DropColumn(
                name: "password_hash_bytes",
                table: "usuarios");

            migrationBuilder.DropColumn(
                name: "password_salt",
                table: "usuarios");

            migrationBuilder.DropColumn(
                name: "cancellation_date",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "invoice_id",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "invoice_status",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "status",
                table: "sales");

            migrationBuilder.RenameColumn(
                name: "sat_unit_code",
                table: "products",
                newName: "unidad_sat");

            migrationBuilder.RenameColumn(
                name: "sat_product_code",
                table: "products",
                newName: "clave_sat");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    action_type = table.Column<int>(type: "INTEGER", nullable: false),
                    authorized_by_supervisor_id = table.Column<string>(type: "TEXT", nullable: true),
                    entity_id = table.Column<string>(type: "TEXT", nullable: false),
                    entity_name = table.Column<string>(type: "TEXT", nullable: false),
                    financial_impact = table.Column<double>(type: "REAL", nullable: false),
                    new_value = table.Column<string>(type: "TEXT", nullable: false),
                    old_value = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    risk_level = table.Column<int>(type: "INTEGER", nullable: false),
                    terminal_name = table.Column<string>(type: "TEXT", nullable: false),
                    timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });
        }
    }
}
