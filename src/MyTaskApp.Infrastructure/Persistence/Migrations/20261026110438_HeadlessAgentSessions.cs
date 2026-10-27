using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HeadlessAgentSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "PromptStartedAt",
                table: "AgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RemoteOperationId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_RemoteOperationId",
                table: "AgentSessions",
                column: "RemoteOperationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_RemoteOperationId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "PromptStartedAt",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "RemoteOperationId",
                table: "AgentSessions");
        }
    }
}
