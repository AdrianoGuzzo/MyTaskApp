using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnboundedDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Vazia de proposito. A descricao perdeu o teto de 4000 caracteres,
            // mas no SQLite a coluna ja era TEXT sem limite: o maxLength so
            // existia no modelo do EF, e o banco nunca o impos. Nada a alterar
            // no arquivo de quem atualiza — so o snapshot precisava acompanhar,
            // senao o MigrateAsync acusaria mudanca de modelo pendente.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
