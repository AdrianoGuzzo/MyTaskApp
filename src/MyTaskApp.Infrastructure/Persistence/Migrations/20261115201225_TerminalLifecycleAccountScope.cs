using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TerminalLifecycleAccountScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TerminalLifecycleOutbox_SessionId_Sequence",
                table: "TerminalLifecycleOutbox");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "TerminalLifecycleOutbox",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalLifecycleOutbox_Scope_SessionId_Sequence",
                table: "TerminalLifecycleOutbox",
                columns: new[] { "Scope", "SessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TerminalLifecycleOutbox_Scope_SessionId_Sequence",
                table: "TerminalLifecycleOutbox");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "TerminalLifecycleOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalLifecycleOutbox_SessionId_Sequence",
                table: "TerminalLifecycleOutbox",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);
        }
    }
}
