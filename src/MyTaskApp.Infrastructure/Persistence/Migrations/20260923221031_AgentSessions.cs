using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProcessStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSessions_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_Status",
                table: "AgentSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskItemId_Active",
                table: "AgentSessions",
                column: "TaskItemId",
                unique: true,
                filter: "\"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskItemId_StartedAt",
                table: "AgentSessions",
                columns: new[] { "TaskItemId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessions");
        }
    }
}
