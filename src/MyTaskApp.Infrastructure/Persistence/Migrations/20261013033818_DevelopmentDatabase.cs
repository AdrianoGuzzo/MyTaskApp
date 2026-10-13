using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Database_AuditId",
                table: "TaskDevelopments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Database_ConnectionId",
                table: "TaskDevelopments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Database_ConnectionName",
                table: "TaskDevelopments",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Database_CopiedAt",
                table: "TaskDevelopments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Database_Host",
                table: "TaskDevelopments",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Database_Name",
                table: "TaskDevelopments",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Database_Port",
                table: "TaskDevelopments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Database_Username",
                table: "TaskDevelopments",
                type: "TEXT",
                maxLength: 63,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Database_AuditId",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_ConnectionId",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_ConnectionName",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_CopiedAt",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_Host",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_Name",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_Port",
                table: "TaskDevelopments");

            migrationBuilder.DropColumn(
                name: "Database_Username",
                table: "TaskDevelopments");
        }
    }
}
