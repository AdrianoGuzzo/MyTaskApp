using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SavedDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationDatabase",
                table: "DatabaseOperationAudits",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDatabase",
                table: "DatabaseOperationAudits",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDatabase",
                table: "DatabaseCopyProfiles",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Database",
                table: "DatabaseConnections",
                type: "TEXT",
                maxLength: 63,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 63);

            migrationBuilder.CreateTable(
                name: "SavedDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 47, nullable: false, collation: "NOCASE"),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    AnonymizationProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedDatabases_AnonymizationProfiles_AnonymizationProfileId",
                        column: x => x.AnonymizationProfileId,
                        principalTable: "AnonymizationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedDatabases_DatabaseConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedDatabases_Alias",
                table: "SavedDatabases",
                column: "Alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedDatabases_AnonymizationProfileId",
                table: "SavedDatabases",
                column: "AnonymizationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedDatabases_ConnectionId",
                table: "SavedDatabases",
                column: "ConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedDatabases");

            migrationBuilder.DropColumn(
                name: "DestinationDatabase",
                table: "DatabaseOperationAudits");

            migrationBuilder.DropColumn(
                name: "SourceDatabase",
                table: "DatabaseOperationAudits");

            migrationBuilder.DropColumn(
                name: "SourceDatabase",
                table: "DatabaseCopyProfiles");

            migrationBuilder.AlterColumn<string>(
                name: "Database",
                table: "DatabaseConnections",
                type: "TEXT",
                maxLength: 63,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 63,
                oldNullable: true);
        }
    }
}
