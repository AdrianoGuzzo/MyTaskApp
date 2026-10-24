using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemotePairings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemotePairings",
                columns: table => new
                {
                    PairingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ControllerSubjectPublicKeyInfo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExecutorDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutorFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemotePairings", x => x.PairingId);
                    table.ForeignKey(
                        name: "FK_RemotePairings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemotePairings_UserId_ExecutorDeviceId_ExpiresAt",
                table: "RemotePairings",
                columns: new[] { "UserId", "ExecutorDeviceId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemotePairings");
        }
    }
}
