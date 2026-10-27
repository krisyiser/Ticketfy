using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class DurableRecommendationWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recommendation_jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaleId = table.Column<string>(type: "TEXT", nullable: false),
                    NextLeft = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRight = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recommendation_jobs_sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_jobs_IsCompleted_Id",
                table: "recommendation_jobs",
                columns: new[] { "IsCompleted", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_jobs_SaleId",
                table: "recommendation_jobs",
                column: "SaleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se permite eliminar los cursores de recomendaciones pendientes. Use una recuperación verificada.");
        }
    }
}
