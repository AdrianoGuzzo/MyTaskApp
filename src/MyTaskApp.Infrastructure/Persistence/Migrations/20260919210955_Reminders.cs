using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Reminder_Anchor",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Reminder_Channels",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Reminder_IsEnabled",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_OffsetTicks",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "Reminder_Repeat",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_RepeatEveryTicks",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_AcknowledgedAtUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Reminder_AcknowledgedBy",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Reminder_Attempt",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_LastFiredAtUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_NextFireAtUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Reminder_WaitingSinceUtc",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReminderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Anchor = table.Column<int>(type: "INTEGER", nullable: false),
                    Offset = table.Column<long>(type: "INTEGER", nullable: false),
                    RepeatUntilAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepeatEvery = table.Column<long>(type: "INTEGER", nullable: false),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    PausedUntilUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderSettings", x => x.Id);
                    table.CheckConstraint("CK_ReminderSettings_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskOccurrences_Reminder_NextFireAtUtc",
                table: "TaskOccurrences",
                column: "Reminder_NextFireAtUtc",
                filter: "\"Reminder_NextFireAtUtc\" IS NOT NULL AND \"Reminder_AcknowledgedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderSettings");

            migrationBuilder.DropIndex(
                name: "IX_TaskOccurrences_Reminder_NextFireAtUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_Anchor",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_Channels",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_IsEnabled",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_OffsetTicks",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_Repeat",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_RepeatEveryTicks",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Reminder_AcknowledgedAtUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_AcknowledgedBy",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_Attempt",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_LastFiredAtUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_NextFireAtUtc",
                table: "TaskOccurrences");

            migrationBuilder.DropColumn(
                name: "Reminder_WaitingSinceUtc",
                table: "TaskOccurrences");
        }
    }
}
