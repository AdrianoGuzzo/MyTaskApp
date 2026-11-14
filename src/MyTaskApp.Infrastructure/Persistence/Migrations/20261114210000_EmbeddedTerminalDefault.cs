using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyTaskApp.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MyTaskAppDbContext))]
[Migration("20261114210000_EmbeddedTerminalDefault")]
public sealed class EmbeddedTerminalDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE AgentSettings SET Hosting = 2 WHERE ProviderId IN ('claude-code', 'codex');");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A preferência anterior não pode ser reconstruída sem sobrescrever escolhas novas.
    }
}
