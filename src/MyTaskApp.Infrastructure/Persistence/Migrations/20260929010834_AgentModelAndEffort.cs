using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentModelAndEffort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Effort",
                table: "AgentSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "AgentSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Effort",
                table: "AgentSettings");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "AgentSettings");
        }
    }
}
