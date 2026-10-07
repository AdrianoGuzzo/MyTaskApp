using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskOccurrenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntries_TaskOccurrences_TaskOccurrenceId",
                        column: x => x.TaskOccurrenceId,
                        principalTable: "TaskOccurrences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_StartedAt",
                table: "TimeEntries",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_TaskOccurrenceId_StartedAt",
                table: "TimeEntries",
                columns: new[] { "TaskOccurrenceId", "StartedAt" });

            // Um cronômetro ativo no app inteiro (ADR-052), escrito à mão porque
            // o EF não modela índice sobre expressão. Um índice único filtrado em
            // EndedAt não seguraria nada: no SQLite os NULLs são distintos num
            // índice único. A expressão vale 1 em toda linha ativa, então a
            // segunda colide com a primeira. O mesmo índice serve a busca do
            // ativo, que usa o mesmo predicado. Ver TimeEntryConfiguration.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_TimeEntries_SingleActive"
                ON "TimeEntries" (("EndedAt" IS NULL))
                WHERE "EndedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeEntries");
        }
    }
}
