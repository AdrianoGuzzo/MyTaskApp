using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteDatabaseCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmedBy",
                table: "DatabaseOperationAudits",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteAllowed",
                table: "DatabaseConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmedBy",
                table: "DatabaseOperationAudits");

            migrationBuilder.DropColumn(
                name: "RemoteAllowed",
                table: "DatabaseConnections");
        }
    }
}
