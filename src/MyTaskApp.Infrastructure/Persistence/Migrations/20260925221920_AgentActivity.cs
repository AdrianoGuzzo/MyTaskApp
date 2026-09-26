using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Arguments",
                table: "AgentSettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 2000);

            migrationBuilder.AddColumn<bool>(
                name: "MonitorActivity",
                table: "AgentSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Activity",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ActivityChangedAt",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityMessage",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSessionId",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HookTokenHash",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonitorActivity",
                table: "AgentSettings");

            migrationBuilder.DropColumn(
                name: "Activity",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ActivityChangedAt",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ActivityMessage",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ExternalSessionId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "HookTokenHash",
                table: "AgentSessions");

            // Linha sem parâmetros salvos (só com o acompanhamento) viraria "",
            // que é "abrir sem parâmetro nenhum" (ADR-033). Sem a linha, volta o
            // padrão do agente — o que ela significava.
            migrationBuilder.Sql("DELETE FROM \"AgentSettings\" WHERE \"Arguments\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Arguments",
                table: "AgentSettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 2000,
                oldNullable: true);
        }
    }
}
