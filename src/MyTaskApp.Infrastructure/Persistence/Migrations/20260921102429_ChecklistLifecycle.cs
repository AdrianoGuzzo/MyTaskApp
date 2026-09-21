using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChecklistLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ArchivedAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ConcludedAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "Tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataRetentionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoArchiveEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoArchiveAfterDays = table.Column<int>(type: "INTEGER", nullable: false),
                    TrashRetentionDays = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataRetentionSettings", x => x.Id);
                    table.CheckConstraint("CK_DataRetentionSettings_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "TaskAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Archived",
                table: "Tasks",
                column: "ArchivedAt",
                filter: "\"ArchivedAt\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ReadyToArchive",
                table: "Tasks",
                column: "ConcludedAt",
                filter: "\"ConcludedAt\" IS NOT NULL AND \"ArchivedAt\" IS NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Trashed",
                table: "Tasks",
                column: "DeletedAt",
                filter: "\"DeletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAuditEntries_OccurredAt",
                table: "TaskAuditEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAuditEntries_TaskId_OccurredAt",
                table: "TaskAuditEntries",
                columns: new[] { "TaskId", "OccurredAt" });

            // Backfill da data de conclusao de quem ja existia.
            //
            // ConcludedAt e mantido pela raiz a cada transicao de ocorrencia, o
            // que so vale dali para a frente: sem isto, todo checklist ja
            // concluido antes da atualizacao ficaria com a coluna nula, nunca
            // apareceria como "Concluido" e jamais entraria no arquivamento
            // automatico — ate alguem reabrir e concluir de novo.
            //
            // A regra e a mesma de TaskItem.RefreshConclusion: nenhuma ocorrencia
            // pendente (Status 0) e pelo menos uma concluida (Status 1), e a data
            // e a mais recente entre as concluidas. Cancelada (Status 2) nao
            // conclui nada.
            //
            // Fazer isto na atualizacao e seguro porque o arquivamento automatico
            // nasce desligado (DataRetentionPolicy.Factory): a coluna passa a
            // dizer a verdade sem que nada seja varrido sem alguem pedir.
            migrationBuilder.Sql(
                """
                UPDATE Tasks
                SET ConcludedAt = (
                    SELECT MAX(completed.CompletedAt)
                    FROM TaskOccurrences AS completed
                    WHERE completed.TaskItemId = Tasks.Id
                      AND completed.Status = 1)
                WHERE NOT EXISTS (
                        SELECT 1
                        FROM TaskOccurrences AS pending
                        WHERE pending.TaskItemId = Tasks.Id
                          AND pending.Status = 0)
                  AND EXISTS (
                        SELECT 1
                        FROM TaskOccurrences AS done
                        WHERE done.TaskItemId = Tasks.Id
                          AND done.Status = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataRetentionSettings");

            migrationBuilder.DropTable(
                name: "TaskAuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Archived",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ReadyToArchive",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Trashed",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ConcludedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "Tasks");
        }
    }
}
