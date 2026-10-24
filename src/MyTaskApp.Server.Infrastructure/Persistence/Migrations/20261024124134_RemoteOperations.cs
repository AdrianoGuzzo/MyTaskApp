using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastContiguousSeq = table.Column<long>(type: "bigint", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    Request = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_RemoteOperations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteOperationEvents",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Step = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: true),
                    Text = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteOperationEvents", x => new { x.OperationId, x.Seq });
                    table.ForeignKey(
                        name: "FK_RemoteOperationEvents_RemoteOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RemoteOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperationEvents_ReceivedAt",
                table: "RemoteOperationEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperations_Status_NotAfter",
                table: "RemoteOperations",
                columns: new[] { "Status", "NotAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperations_UserId_ReceivedAt",
                table: "RemoteOperations",
                columns: new[] { "UserId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperations_UserId_TargetDeviceId_Status",
                table: "RemoteOperations",
                columns: new[] { "UserId", "TargetDeviceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteOperationEvents");

            migrationBuilder.DropTable(
                name: "RemoteOperations");
        }
    }
}
