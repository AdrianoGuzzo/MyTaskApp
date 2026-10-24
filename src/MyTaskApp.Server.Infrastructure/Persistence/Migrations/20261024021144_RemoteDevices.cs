using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceCapabilities",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Roles = table.Column<int>(type: "integer", nullable: false),
                    Capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCapabilities", x => new { x.UserId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_DeviceCapabilities_Devices_UserId_DeviceId",
                        columns: x => new { x.UserId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "UserId", "DeviceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceKeys",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectPublicKeyInfo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceKeys", x => new { x.UserId, x.DeviceId, x.Fingerprint });
                    table.ForeignKey(
                        name: "FK_DeviceKeys_Devices_UserId_DeviceId",
                        columns: x => new { x.UserId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "UserId", "DeviceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceKeys_UserId_DeviceId",
                table: "DeviceKeys",
                columns: new[] { "UserId", "DeviceId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCapabilities");

            migrationBuilder.DropTable(
                name: "DeviceKeys");
        }
    }
}
