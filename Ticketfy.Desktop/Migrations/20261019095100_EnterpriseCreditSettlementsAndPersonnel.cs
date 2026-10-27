using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class EnterpriseCreditSettlementsAndPersonnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CashierUserId",
                table: "shift_notes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "shift_notes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RegisterId",
                table: "shift_notes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftId",
                table: "shift_notes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_credit_settlements",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetDocumentId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    TenderKind = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_credit_settlements", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "shift_note_revisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NoteId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    NoteText = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_note_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shift_note_revisions_shift_notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "shift_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shift_notes_RegisterId_created_at",
                table: "shift_notes",
                columns: new[] { "RegisterId", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_account_credit_settlements_AccountKind_AccountId_CreatedAtUtc",
                table: "account_credit_settlements",
                columns: new[] { "AccountKind", "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_credit_settlements_ExternalReference",
                table: "account_credit_settlements",
                column: "ExternalReference",
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shift_note_revisions_NoteId_CreatedAtUtc",
                table: "shift_note_revisions",
                columns: new[] { "NoteId", "CreatedAtUtc" });

            foreach (string table in new[] { "account_credit_settlements", "shift_note_revisions" })
                foreach (string action in new[] { "UPDATE", "DELETE" })
                    migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_{action} BEFORE {action} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Posted records are immutable'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_credit_settlements");

            migrationBuilder.DropTable(
                name: "shift_note_revisions");

            migrationBuilder.DropIndex(
                name: "IX_shift_notes_RegisterId_created_at",
                table: "shift_notes");

            migrationBuilder.DropColumn(
                name: "CashierUserId",
                table: "shift_notes");

            migrationBuilder.DropColumn(
                name: "IsCancelled",
                table: "shift_notes");

            migrationBuilder.DropColumn(
                name: "RegisterId",
                table: "shift_notes");

            migrationBuilder.DropColumn(
                name: "ShiftId",
                table: "shift_notes");
        }
    }
}
