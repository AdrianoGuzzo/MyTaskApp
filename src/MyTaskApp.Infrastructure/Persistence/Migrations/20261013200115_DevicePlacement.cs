using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicePlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlacementDeviceId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlacementKind",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReceivedAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceivedFromDeviceId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlacementDeviceId",
                table: "Tags",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlacementKind",
                table: "Tags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormerDeviceIds",
                table: "SyncState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KnownDevices",
                table: "SyncState",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlacementDeviceId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "PlacementKind",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReceivedFromDeviceId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "PlacementDeviceId",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "PlacementKind",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "FormerDeviceIds",
                table: "SyncState");

            migrationBuilder.DropColumn(
                name: "KnownDevices",
                table: "SyncState");
        }
    }
}
