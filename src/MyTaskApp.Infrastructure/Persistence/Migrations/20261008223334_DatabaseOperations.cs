using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, collation: "NOCASE"),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Database = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Environment = table.Column<int>(type: "INTEGER", nullable: false),
                    SslMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false),
                    SecretReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseOperationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationType = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceConnectionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DestinationConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DestinationConnectionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProfileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AnonymizationProfile = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ToolVersions = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SourceDatabaseVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DestinationDatabaseVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RowsProcessed = table.Column<long>(type: "INTEGER", nullable: true),
                    DumpSize = table.Column<long>(type: "INTEGER", nullable: true),
                    AnonymousDumpSize = table.Column<long>(type: "INTEGER", nullable: true),
                    Duration = table.Column<long>(type: "INTEGER", nullable: true),
                    MaskedColumnsCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseOperationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnonymizationProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymizationProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnonymizationProfiles_DatabaseConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnonymizationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Schema = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Table = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Column = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Expression = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Sensitivity = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfirmedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymizationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnonymizationRules_AnonymizationProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "AnonymizationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseCopyProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, collation: "NOCASE"),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnonymizationProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequireAnonymization = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeSchema = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeData = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecreateDestination = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerifyAfterRestore = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepAnonymizedArtifact = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseCopyProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseCopyProfiles_AnonymizationProfiles_AnonymizationProfileId",
                        column: x => x.AnonymizationProfileId,
                        principalTable: "AnonymizationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseCopyProfiles_DatabaseConnections_DestinationConnectionId",
                        column: x => x.DestinationConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseCopyProfiles_DatabaseConnections_SourceConnectionId",
                        column: x => x.SourceConnectionId,
                        principalTable: "DatabaseConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnonymizationProfiles_ConnectionId",
                table: "AnonymizationProfiles",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnonymizationProfiles_Name",
                table: "AnonymizationProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnonymizationRules_ProfileId_Schema_Table_Column",
                table: "AnonymizationRules",
                columns: new[] { "ProfileId", "Schema", "Table", "Column" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseConnections_Name",
                table: "DatabaseConnections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCopyProfiles_AnonymizationProfileId",
                table: "DatabaseCopyProfiles",
                column: "AnonymizationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCopyProfiles_DestinationConnectionId",
                table: "DatabaseCopyProfiles",
                column: "DestinationConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCopyProfiles_Name",
                table: "DatabaseCopyProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseCopyProfiles_SourceConnectionId",
                table: "DatabaseCopyProfiles",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseOperationAudits_Running",
                table: "DatabaseOperationAudits",
                column: "Status",
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseOperationAudits_StartedAt",
                table: "DatabaseOperationAudits",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnonymizationRules");

            migrationBuilder.DropTable(
                name: "DatabaseCopyProfiles");

            migrationBuilder.DropTable(
                name: "DatabaseOperationAudits");

            migrationBuilder.DropTable(
                name: "AnonymizationProfiles");

            migrationBuilder.DropTable(
                name: "DatabaseConnections");
        }
    }
}
