using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Data;

#nullable disable

namespace Ticketfy.Desktop.Migrations.Audit;

[DbContext(typeof(AuditDbContext))]
[Migration("20261018190000_AddAuditIntegrityChain")]
public partial class AddAuditIntegrityChain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "entry_hash", table: "audit_logs", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "previous_hash", table: "audit_logs", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>(name: "schema_version", table: "audit_logs", type: "INTEGER", nullable: false, defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "entry_hash", table: "audit_logs");
        migrationBuilder.DropColumn(name: "previous_hash", table: "audit_logs");
        migrationBuilder.DropColumn(name: "schema_version", table: "audit_logs");
    }
}
