using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ticketfy.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep every ambiguous historical entry; never guess an exit time or paid hours.
            // Materialize the set before updating so no duplicate is accidentally left active.
            migrationBuilder.Sql("CREATE TEMP TABLE ticketfy_duplicate_attendance (id TEXT PRIMARY KEY);");
            migrationBuilder.Sql("""
                INSERT INTO ticketfy_duplicate_attendance
                SELECT id FROM asistencias WHERE check_out_time IS NULL AND status = 0
                AND usuario_id IN (SELECT usuario_id FROM asistencias WHERE check_out_time IS NULL AND status = 0
                                   GROUP BY usuario_id HAVING COUNT(*) > 1);
                """);
            migrationBuilder.Sql("""
                INSERT INTO transaction_journal
                    (id, operation_id, event_type, entity_id, amount, details, created_at_utc, CashierUserId, RegisterId, ShiftId)
                SELECT 'ATTENDANCE:DUP:' || id, 'ATTENDANCE:DUP:' || id, 'ATTENDANCE_DUPLICATE_REVIEW', id, 0,
                    'Entradas históricas simultáneas. Revisión obligatoria; sin salida ni horas inventadas.',
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'), 'system:migration', 'system:migration', NULL
                FROM ticketfy_duplicate_attendance;
                """);
            migrationBuilder.Sql("UPDATE asistencias SET status = 2 WHERE id IN (SELECT id FROM ticketfy_duplicate_attendance);");
            migrationBuilder.Sql("DROP TABLE ticketfy_duplicate_attendance;");
            migrationBuilder.DropIndex(
                name: "IX_asistencias_usuario_id",
                table: "asistencias");

            migrationBuilder.CreateIndex(
                name: "UX_attendance_active_user",
                table: "asistencias",
                column: "usuario_id",
                unique: true,
                filter: "check_out_time IS NULL AND status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("No se permite reactivar asistencias históricas ambiguas. Restaure una copia compatible en otra carpeta.");
        }
    }
}
