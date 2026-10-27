using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class PersistCredentialAttemptLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credential_attempts",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LockedUntilUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_attempts", x => x.Subject);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se permite eliminar límites de autenticación activos mediante downgrade.");
        }
    }
}
