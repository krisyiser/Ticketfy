using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class PersistentLedgerPagingSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PostingSequence",
                table: "inventory_movements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PostingSequence",
                table: "account_entries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            foreach (string table in new[] { "inventory_movements", "account_entries" })
            {
                // Assign a stable traversal order only. Historical dates, amounts and attribution are unchanged.
                migrationBuilder.Sql($"DROP TRIGGER immutable_{table}_UPDATE;");
                migrationBuilder.Sql($"""
                    WITH positions AS (
                      SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAtUtc, Id) AS position FROM "{table}"
                    )
                    UPDATE "{table}" SET PostingSequence = (SELECT position FROM positions WHERE positions.Id = "{table}".Id);
                    """);
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_UPDATE BEFORE UPDATE ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Posted records are immutable; use a compensating operation'); END;");
                migrationBuilder.Sql($"CREATE TRIGGER sequenced_{table}_insert BEFORE INSERT ON \"{table}\" WHEN NEW.PostingSequence <= 0 BEGIN SELECT RAISE(ABORT, 'A ledger posting requires a positive sequence'); END;");
            }

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_PostingSequence",
                table: "inventory_movements",
                column: "PostingSequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId_PostingSequence",
                table: "inventory_movements",
                columns: new[] { "ProductId", "PostingSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_AccountKind_AccountId_PostingSequence",
                table: "account_entries",
                columns: new[] { "AccountKind", "AccountId", "PostingSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_account_entries_PostingSequence",
                table: "account_entries",
                column: "PostingSequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se permite eliminar el orden persistente de registros publicados.");
        }
    }
}
