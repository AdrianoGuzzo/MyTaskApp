using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuickCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Escrito à mão (ADR-051): o EF geraria false, e o padrão de um comando
            // é a janela do terminal ficar aberta. O modelo não tem HasDefaultValue
            // de propósito — ver DevelopmentCommandConfiguration.
            migrationBuilder.AddColumn<bool>(
                name: "KeepTerminalOpen",
                table: "DevelopmentCommands",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "DevelopmentCommands",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "DevelopmentCommands",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresConfirmation",
                table: "DevelopmentCommands",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WorkingDirectory",
                table: "DevelopmentCommands",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DevelopmentCommandParameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DevelopmentCommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    Options = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentCommandParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentCommandParameters_DevelopmentCommands_DevelopmentCommandId",
                        column: x => x.DevelopmentCommandId,
                        principalTable: "DevelopmentCommands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagDirectoryCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagDirectoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DevelopmentCommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CommandOverride = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    WorkingDirectoryOverride = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagDirectoryCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagDirectoryCommands_DevelopmentCommands_DevelopmentCommandId",
                        column: x => x.DevelopmentCommandId,
                        principalTable: "DevelopmentCommands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagDirectoryCommands_TagDirectories_TagDirectoryId",
                        column: x => x.TagDirectoryId,
                        principalTable: "TagDirectories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommandExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskDevelopmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DevelopmentCommandId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TagDirectoryCommandId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CommandName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CommandLine = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepTerminalOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProcessStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Output = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: true),
                    ErrorOutput = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommandExecutions_DevelopmentCommands_DevelopmentCommandId",
                        column: x => x.DevelopmentCommandId,
                        principalTable: "DevelopmentCommands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CommandExecutions_TagDirectoryCommands_TagDirectoryCommandId",
                        column: x => x.TagDirectoryCommandId,
                        principalTable: "TagDirectoryCommands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CommandExecutions_TaskDevelopments_TaskDevelopmentId",
                        column: x => x.TaskDevelopmentId,
                        principalTable: "TaskDevelopments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CommandExecutions_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_Active",
                table: "CommandExecutions",
                column: "Status",
                filter: "\"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_DevelopmentCommandId",
                table: "CommandExecutions",
                column: "DevelopmentCommandId");

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_TagDirectoryCommandId",
                table: "CommandExecutions",
                column: "TagDirectoryCommandId");

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_TaskDevelopmentId_StartedAt",
                table: "CommandExecutions",
                columns: new[] { "TaskDevelopmentId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_TaskItemId_StartedAt",
                table: "CommandExecutions",
                columns: new[] { "TaskItemId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentCommandParameters_DevelopmentCommandId_Order",
                table: "DevelopmentCommandParameters",
                columns: new[] { "DevelopmentCommandId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_TagDirectoryCommands_DevelopmentCommandId",
                table: "TagDirectoryCommands",
                column: "DevelopmentCommandId");

            migrationBuilder.CreateIndex(
                name: "IX_TagDirectoryCommands_TagDirectoryId_DevelopmentCommandId",
                table: "TagDirectoryCommands",
                columns: new[] { "TagDirectoryId", "DevelopmentCommandId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TagDirectoryCommands_TagDirectoryId_Order",
                table: "TagDirectoryCommands",
                columns: new[] { "TagDirectoryId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandExecutions");

            migrationBuilder.DropTable(
                name: "DevelopmentCommandParameters");

            migrationBuilder.DropTable(
                name: "TagDirectoryCommands");

            migrationBuilder.DropColumn(
                name: "KeepTerminalOpen",
                table: "DevelopmentCommands");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "DevelopmentCommands");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "DevelopmentCommands");

            migrationBuilder.DropColumn(
                name: "RequiresConfirmation",
                table: "DevelopmentCommands");

            migrationBuilder.DropColumn(
                name: "WorkingDirectory",
                table: "DevelopmentCommands");
        }
    }
}
