using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteOperationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteOperationJournal",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControllerDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControllerNickname = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Envelope = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportedStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Step = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NextSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    AckedSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteOperationJournal", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "RemoteOperationJournalEvents",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteOperationJournalEvents", x => new { x.OperationId, x.Seq });
                    table.ForeignKey(
                        name: "FK_RemoteOperationJournalEvents_RemoteOperationJournal_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RemoteOperationJournal",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperationJournal_ControllerDeviceId_ReceivedAt",
                table: "RemoteOperationJournal",
                columns: new[] { "ControllerDeviceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteOperationJournal_Status",
                table: "RemoteOperationJournal",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteOperationJournalEvents");

            migrationBuilder.DropTable(
                name: "RemoteOperationJournal");
        }
    }
}
