using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskExternalIssueLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "External_Id",
                table: "Tasks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "External_IssueType",
                table: "Tasks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "External_Provider",
                table: "Tasks",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "External_Status",
                table: "Tasks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "External_SyncedAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "External_Title",
                table: "Tasks",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "External_Url",
                table: "Tasks",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "External_Id",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_IssueType",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_Provider",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_Status",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_SyncedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_Title",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "External_Url",
                table: "Tasks");
        }
    }
}
