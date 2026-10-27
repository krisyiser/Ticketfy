using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class ProtectInventorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Older quantity values used the two-decimal money converter. Preserve their represented
            // quantity while switching to thousandths; do not fabricate previously lost precision.
            migrationBuilder.Sql("CREATE TEMP TABLE ticketfy_snapshot_quantity_guard (valid INTEGER CHECK (valid=1));");
            migrationBuilder.Sql("INSERT INTO ticketfy_snapshot_quantity_guard SELECT CASE WHEN EXISTS (SELECT 1 FROM InventorySnapshotItems WHERE typeof(Quantity) <> 'integer' OR Quantity > 922337203685477580 OR Quantity < -922337203685477580) THEN 0 ELSE 1 END;");
            migrationBuilder.Sql("DROP TABLE ticketfy_snapshot_quantity_guard;");
            migrationBuilder.Sql("UPDATE InventorySnapshotItems SET Quantity = Quantity * 10;");
            foreach (string table in new[] { "InventorySnapshots", "InventorySnapshotItems" })
            {
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_update BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT, 'Immutable inventory capture'); END;");
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_delete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'Immutable inventory capture'); END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrade would lose fractional quantities and remove evidence protections.
            throw new System.NotSupportedException("No se permite degradar capturas de inventario. Restaure un respaldo compatible en una carpeta nueva.");
        }
    }
}
