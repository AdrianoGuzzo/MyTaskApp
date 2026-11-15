using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceUnifiedPushWithFcm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As URLs do distribuidor antigo não são tokens Firebase; o aparelho se registra novamente.
            migrationBuilder.Sql("DELETE FROM \"RemotePushDeliveries\" AS d USING \"RemotePushDestinations\" AS t WHERE d.\"UserId\" = t.\"UserId\" AND d.\"DeviceId\" = t.\"DeviceId\" AND t.\"Provider\" = 0");
            migrationBuilder.Sql("DELETE FROM \"RemotePushDestinations\" WHERE \"Provider\" = 0");
            migrationBuilder.DropColumn(
                name: "AuthSecret",
                table: "RemotePushDestinations");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "RemotePushDestinations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ao reverter, o cliente também precisará registrar o provedor anterior novamente.
            migrationBuilder.Sql("DELETE FROM \"RemotePushDeliveries\" AS d USING \"RemotePushDestinations\" AS t WHERE d.\"UserId\" = t.\"UserId\" AND d.\"DeviceId\" = t.\"DeviceId\" AND t.\"Provider\" = 2");
            migrationBuilder.Sql("DELETE FROM \"RemotePushDestinations\" WHERE \"Provider\" = 2");
            migrationBuilder.AddColumn<string>(
                name: "AuthSecret",
                table: "RemotePushDestinations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKey",
                table: "RemotePushDestinations",
                type: "text",
                nullable: true);
        }
    }
}
