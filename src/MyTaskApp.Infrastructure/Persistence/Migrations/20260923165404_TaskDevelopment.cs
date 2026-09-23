using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskDevelopment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskDevelopments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SourceBranch = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StatusChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDevelopments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskDevelopments_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDevelopments_TaskItemId",
                table: "TaskDevelopments",
                column: "TaskItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskDevelopments");
        }
    }
}
