using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class DurableCreditRefundRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credit_refund_requests",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    TenderKind = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CashierUserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_refund_requests", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "credit_refund_resolutions",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    FundsMoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ApproverUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_refund_resolutions", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_credit_refund_resolutions_credit_refund_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "credit_refund_requests",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credit_refund_requests_AccountKind_AccountId_SourceDocumentId",
                table: "credit_refund_requests",
                columns: new[] { "AccountKind", "AccountId", "SourceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_refund_requests_ShiftId",
                table: "credit_refund_requests",
                column: "ShiftId");
            foreach (string table in new[] { "credit_refund_requests", "credit_refund_resolutions" })
            {
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_update BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT, 'Immutable commercial record'); END;");
                migrationBuilder.Sql($"CREATE TRIGGER immutable_{table}_delete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'Immutable commercial record'); END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_refund_resolutions");

            migrationBuilder.DropTable(
                name: "credit_refund_requests");
        }
    }
}
