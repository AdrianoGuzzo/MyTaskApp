using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultipleDevelopments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskDevelopments_TaskItemId",
                table: "TaskDevelopments");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_TaskItemId_Active",
                table: "AgentSessions");

            migrationBuilder.AddColumn<Guid>(
                name: "TaskDevelopmentId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDevelopments_TaskItemId",
                table: "TaskDevelopments",
                column: "TaskItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskDevelopmentId_Active",
                table: "AgentSessions",
                column: "TaskDevelopmentId",
                unique: true,
                filter: "\"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskDevelopmentId_StartedAt",
                table: "AgentSessions",
                columns: new[] { "TaskDevelopmentId", "StartedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_TaskDevelopments_TaskDevelopmentId",
                table: "AgentSessions",
                column: "TaskDevelopmentId",
                principalTable: "TaskDevelopments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Até aqui, uma tarefa tinha no máximo um ambiente: a sessão de agente
            // dela era desse ambiente. Por último, depois de o SQLite reconstruir a
            // tabela para a chave estrangeira.
            migrationBuilder.Sql(
                """
                UPDATE "AgentSessions"
                SET "TaskDevelopmentId" = (
                    SELECT "d"."Id" FROM "TaskDevelopments" AS "d"
                    WHERE "d"."TaskItemId" = "AgentSessions"."TaskItemId")
                WHERE "TaskDevelopmentId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Voltar só funciona enquanto nenhuma tarefa tiver mais de um ambiente:
            // o índice único de antes recusa a segunda linha.
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_TaskDevelopments_TaskDevelopmentId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_TaskDevelopments_TaskItemId",
                table: "TaskDevelopments");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_TaskDevelopmentId_Active",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_TaskDevelopmentId_StartedAt",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "TaskDevelopmentId",
                table: "AgentSessions");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDevelopments_TaskItemId",
                table: "TaskDevelopments",
                column: "TaskItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskItemId_Active",
                table: "AgentSessions",
                column: "TaskItemId",
                unique: true,
                filter: "\"Status\" IN (1, 2)");
        }
    }
}
