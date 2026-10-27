using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseOperationOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerProcessId",
                table: "DatabaseOperationAudits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OwnerProcessStartedAt",
                table: "DatabaseOperationAudits",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerProcessId",
                table: "DatabaseOperationAudits");

            migrationBuilder.DropColumn(
                name: "OwnerProcessStartedAt",
                table: "DatabaseOperationAudits");
        }
    }
}
