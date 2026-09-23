using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevelopmentCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false, collation: "NOCASE"),
                    Command = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskDevelopmentCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskDevelopmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDevelopmentCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskDevelopmentCommands_TaskDevelopments_TaskDevelopmentId",
                        column: x => x.TaskDevelopmentId,
                        principalTable: "TaskDevelopments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentCommands_Alias",
                table: "DevelopmentCommands",
                column: "Alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDevelopmentCommands_TaskDevelopmentId_Order",
                table: "TaskDevelopmentCommands",
                columns: new[] { "TaskDevelopmentId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevelopmentCommands");

            migrationBuilder.DropTable(
                name: "TaskDevelopmentCommands");
        }
    }
}
