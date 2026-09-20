using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ScheduledTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskOccurrences_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_CompletedAt",
                table: "TaskOccurrences",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_ScheduledDate_Status",
                table: "TaskOccurrences",
                columns: new[] { "ScheduledDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_TaskItemId_ScheduledDate_ScheduledTime",
                table: "TaskOccurrences",
                columns: new[] { "TaskItemId", "ScheduledDate", "ScheduledTime" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskOccurrences");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
