using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteAgentSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentSessionId",
                table: "RemoteOperations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentSeenAt",
                table: "DeviceCapabilities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RemoteAgentSessions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Activity = table.Column<int>(type: "integer", nullable: false),
                    ActivityMessage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CurrentOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAgentSessions", x => new { x.UserId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_RemoteAgentSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperations_UserId_AgentSessionId_ReceivedAt",
                table: "RemoteOperations",
                columns: new[] { "UserId", "AgentSessionId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAgentSessions_ReportedAt",
                table: "RemoteAgentSessions",
                column: "ReportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAgentSessions_UserId_DeviceId_Status",
                table: "RemoteAgentSessions",
                columns: new[] { "UserId", "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAgentSessions_UserId_UpdatedAt",
                table: "RemoteAgentSessions",
                columns: new[] { "UserId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteAgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_RemoteOperations_UserId_AgentSessionId_ReceivedAt",
                table: "RemoteOperations");

            migrationBuilder.DropColumn(
                name: "AgentSessionId",
                table: "RemoteOperations");

            migrationBuilder.DropColumn(
                name: "AgentSeenAt",
                table: "DeviceCapabilities");
        }
    }
}
