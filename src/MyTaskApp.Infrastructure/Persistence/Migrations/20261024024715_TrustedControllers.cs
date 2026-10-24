using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrustedControllers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrustedControllers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControllerDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nickname = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectPublicKeyInfo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PairingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedControllers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrustedControllers_ControllerDeviceId",
                table: "TrustedControllers",
                column: "ControllerDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedControllers_Fingerprint",
                table: "TrustedControllers",
                column: "Fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrustedControllers");
        }
    }
}
