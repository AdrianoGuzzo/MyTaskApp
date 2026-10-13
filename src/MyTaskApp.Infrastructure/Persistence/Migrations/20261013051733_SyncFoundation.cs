using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LocalValue = table.Column<string>(type: "TEXT", nullable: true),
                    ServerValue = table.Column<string>(type: "TEXT", nullable: true),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DetectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncEntityStates",
                columns: table => new
                {
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Snapshot = table.Column<string>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByDeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OriginCreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedByDeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewedVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ApplyErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApplyError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncEntityStates", x => new { x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateTable(
                name: "SyncOutbox",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Fields = table.Column<string>(type: "TEXT", nullable: false),
                    OriginCreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutbox", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerBaseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserDisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinkedDeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ClientSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPushAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPullAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                    table.CheckConstraint("CK_SyncState_SingleRow", "Id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_DetectedAt",
                table: "SyncConflicts",
                column: "DetectedAt",
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncEntityStates_ApplyErrorCode",
                table: "SyncEntityStates",
                column: "ApplyErrorCode",
                filter: "\"ApplyErrorCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_EntityType_EntityId",
                table: "SyncOutbox",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_OpId",
                table: "SyncOutbox",
                column: "OpId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncConflicts");

            migrationBuilder.DropTable(
                name: "SyncEntityStates");

            migrationBuilder.DropTable(
                name: "SyncOutbox");

            migrationBuilder.DropTable(
                name: "SyncState");
        }
    }
}
