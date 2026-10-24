using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentCatalogs",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Catalog = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCatalogs", x => new { x.UserId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_AgentCatalogs_Devices_UserId_DeviceId",
                        columns: x => new { x.UserId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "UserId", "DeviceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteApprovals",
                columns: table => new
                {
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Request = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Response = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    RespondedByDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteApprovals", x => x.ApprovalId);
                    table.ForeignKey(
                        name: "FK_RemoteApprovals_RemoteOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RemoteOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteApprovals_OperationId",
                table: "RemoteApprovals",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteApprovals_State_ExpiresAt",
                table: "RemoteApprovals",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteApprovals_UserId_State_ExpiresAt",
                table: "RemoteApprovals",
                columns: new[] { "UserId", "State", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCatalogs");

            migrationBuilder.DropTable(
                name: "RemoteApprovals");
        }
    }
}
