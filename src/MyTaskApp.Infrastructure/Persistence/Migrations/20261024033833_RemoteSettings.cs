using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcceptControl = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSettings", x => x.Id);
                    table.CheckConstraint("CK_RemoteSettings_SingleRow", "Id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteSettings");
        }
    }
}
