using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sem backfill, e isso e decisao — nao esquecimento.
            //
            // Position nula quer dizer "nunca foi arrastada", e a secao ordena
            // essa linha pelo criterio de sempre. Nula em todo mundo significa,
            // portanto, que a lista de quem atualiza sai exatamente como saia
            // antes: a atualizacao nao renumera nada, nao reordena nada e nao
            // move nenhuma tarefa de lugar.
            //
            // Mesma razao do ADR-014 ("Upgrade nao arma nada") e do ADR-020
            // (arquivamento automatico nasce desligado): a primeira abertura
            // depois de uma atualizacao nao pode mexer no que ninguem pediu.
            // Semear posicoes aqui — por ordem alfabetica, por exemplo —
            // congelaria para sempre um criterio que hoje e so o desempate.
            //
            // Ha teste em UpgradePreservationTests fixando isto.
            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "TaskOccurrences",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Position",
                table: "TaskOccurrences");
        }
    }
}
