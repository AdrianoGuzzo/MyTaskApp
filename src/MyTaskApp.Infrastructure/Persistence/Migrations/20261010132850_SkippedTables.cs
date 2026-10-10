using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SkippedTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnonymizationSkippedTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Schema = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Table = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    ConfirmedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymizationSkippedTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnonymizationSkippedTables_AnonymizationProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "AnonymizationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnonymizationSkippedTables_ProfileId_Schema_Table",
                table: "AnonymizationSkippedTables",
                columns: new[] { "ProfileId", "Schema", "Table" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnonymizationSkippedTables");
        }
    }
}
