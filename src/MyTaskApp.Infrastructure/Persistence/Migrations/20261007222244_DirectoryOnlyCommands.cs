using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectoryOnlyCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Alias",
                table: "DevelopmentCommands",
                type: "TEXT",
                maxLength: 40,
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 40,
                oldCollation: "NOCASE");

            migrationBuilder.AddColumn<Guid>(
                name: "TagDirectoryId",
                table: "DevelopmentCommands",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentCommands_TagDirectoryId",
                table: "DevelopmentCommands",
                column: "TagDirectoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_DevelopmentCommands_TagDirectories_TagDirectoryId",
                table: "DevelopmentCommands",
                column: "TagDirectoryId",
                principalTable: "TagDirectories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Escrito à mão: sem apelido, os comandos só de diretório (ADR-054)
            // voltariam todos com "" e esbarrariam no índice único do Alias.
            migrationBuilder.Sql("DELETE FROM \"DevelopmentCommands\" WHERE \"TagDirectoryId\" IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_DevelopmentCommands_TagDirectories_TagDirectoryId",
                table: "DevelopmentCommands");

            migrationBuilder.DropIndex(
                name: "IX_DevelopmentCommands_TagDirectoryId",
                table: "DevelopmentCommands");

            migrationBuilder.DropColumn(
                name: "TagDirectoryId",
                table: "DevelopmentCommands");

            migrationBuilder.AlterColumn<string>(
                name: "Alias",
                table: "DevelopmentCommands",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "",
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 40,
                oldNullable: true,
                oldCollation: "NOCASE");
        }
    }
}
