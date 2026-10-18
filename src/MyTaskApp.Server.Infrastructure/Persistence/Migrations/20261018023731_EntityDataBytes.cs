using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EntityDataBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataBytes",
                table: "SyncEntities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // O texto do jsonb tem espaços que o servidor não grava: conta um pouco
            // a mais, e a próxima escrita de cada entidade acerta (ADR-072).
            migrationBuilder.Sql("UPDATE \"SyncEntities\" SET \"DataBytes\" = octet_length(\"Data\"::text);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataBytes",
                table: "SyncEntities");
        }
    }
}
