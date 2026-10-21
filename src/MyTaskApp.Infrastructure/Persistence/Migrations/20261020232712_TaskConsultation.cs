using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskConsultation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsultationTokenHash",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TaskConsultations",
                columns: table => new
                {
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DatabaseConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DatabaseName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Effort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastQuestion = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskConsultations", x => x.TaskItemId);
                    table.ForeignKey(
                        name: "FK_TaskConsultations_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskConsultationSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskConsultationSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskConsultationSources_TaskConsultations_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "TaskConsultations",
                        principalColumn: "TaskItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ConsultationTokenHash",
                table: "AgentSessions",
                column: "ConsultationTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskItemId_ConsultationActive",
                table: "AgentSessions",
                column: "TaskItemId",
                unique: true,
                filter: "\"Kind\" = 1 AND \"Status\" IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentSessions_ConsultationWithoutDevelopment",
                table: "AgentSessions",
                sql: "\"Kind\" <> 1 OR \"TaskDevelopmentId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskConsultationSources_TaskItemId_Kind_ReferenceId",
                table: "TaskConsultationSources",
                columns: new[] { "TaskItemId", "Kind", "ReferenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskConsultationSources");

            migrationBuilder.DropTable(
                name: "TaskConsultations");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_ConsultationTokenHash",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_TaskItemId_ConsultationActive",
                table: "AgentSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentSessions_ConsultationWithoutDevelopment",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ConsultationTokenHash",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AgentSessions");
        }
    }
}
