using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommandSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDetached",
                table: "SyncEntityStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Escrito à mão (ADR-066): os comandos de antes já iam para todos os
            // computadores e continuam indo. O novo nasce deste computador pelo
            // domínio — o modelo não tem HasDefaultValue de propósito, ver
            // DevelopmentCommandConfiguration.
            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "DevelopmentCommands",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDetached",
                table: "SyncEntityStates");

            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "DevelopmentCommands");
        }
    }
}
