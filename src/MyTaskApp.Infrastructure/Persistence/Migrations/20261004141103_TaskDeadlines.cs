using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeadlineAlerts",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EstimateTicks",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextAction",
                table: "Tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeadlineAlert_LastAlertAtUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeadlineAlert_LastStage",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DeadlineAlert_SnoozedUntilUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Deadline_Date",
                table: "TaskOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Deadline_Time",
                table: "TaskOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeadlineSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Stages = table.Column<int>(type: "INTEGER", nullable: false),
                    OverdueRepeatEveryTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    DefaultTime = table.Column<TimeOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadlineSettings", x => x.Id);
                    table.CheckConstraint("CK_DeadlineSettings_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_Deadline",
                table: "TaskOccurrences",
                column: "Deadline_Date",
                filter: "\"Deadline_Date\" IS NOT NULL AND \"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadlineSettings");

            migrationBuilder.DropIndex(
                name: "IX_TaskOccurrences_Deadline",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "DeadlineAlerts",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EstimateTicks",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "NextAction",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeadlineAlert_LastAlertAtUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "DeadlineAlert_LastStage",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "DeadlineAlert_SnoozedUntilUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Deadline_Date",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Deadline_Time",
                table: "TaskOccurrences");
        }
    }
}
