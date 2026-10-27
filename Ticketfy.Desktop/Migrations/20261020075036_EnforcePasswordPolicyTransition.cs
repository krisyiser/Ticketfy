using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePasswordPolicyTransition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "password_policy_version",
                table: "usuarios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se permite eliminar el estado de validación de contraseñas. Use una recuperación verificada.");
        }
    }
}
