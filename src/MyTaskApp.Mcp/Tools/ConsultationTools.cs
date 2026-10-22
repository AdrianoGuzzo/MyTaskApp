using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// As ferramentas da Consulta Inteligente (ADR-081): ler o banco escolhido na
/// aba da tarefa, e nada mais. Só o Claude aberto pela consulta as enxerga,
/// com o segredo da sessão; o token do servidor não as chama.
/// </summary>
/// <remarks>
/// <para>
/// Nenhuma recebe conexão, servidor nem banco: o banco é o da consulta da
/// tarefa da sessão, conferido de novo a cada chamada (sessão ativa, política,
/// usuário só leitura).
/// </para>
/// <para>
/// <c>consultation_query</c> é a única exceção à regra de que nenhuma
/// ferramenta do app aceita SQL: ela vai como subconsulta de um <c>SELECT</c>,
/// numa sessão e numa transação só leitura, com um usuário que só lê — quem
/// recusa escrita é o PostgreSQL, e não esta classe.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ConsultationTools(McpGateway gateway)
{
    [McpServerTool(Name = ConsultationToolNames.DescribeDatabase, Title = "Descrever o banco da consulta", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "O banco desta Consulta Inteligente: nome, PostgreSQL e versão, ambiente (sempre uma cópia local), os schemas legíveis " +
        "e os limites de cada consulta. Comece por aqui.")]
    public Task<ConsultationDatabaseDescription> DescribeDatabaseAsync(CancellationToken cancellationToken = default) =>
        gateway.ConsultAsync(
            ConsultationToolNames.DescribeDatabase,
            (runner, access, token) => runner.RunAsync<DescribeConsultationDatabaseHandler, ConsultationDatabaseDescription>(
                (handler, cancel) => handler.HandleAsync(new DescribeConsultationDatabase(access.SessionId), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = ConsultationToolNames.ListTables, Title = "Listar tabelas da consulta", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("As tabelas e views do banco desta consulta, com as linhas estimadas e o comentário de cada uma.")]
    public Task<IReadOnlyList<ConsultationTable>> ListTablesAsync(
        [Description("Só deste schema; vazio = todos os legíveis.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        gateway.ConsultAsync(
            ConsultationToolNames.ListTables,
            (runner, access, token) => runner.RunAsync<ListConsultationTablesHandler, IReadOnlyList<ConsultationTable>>(
                (handler, cancel) => handler.HandleAsync(new ListConsultationTables(access.SessionId, schema), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = ConsultationToolNames.DescribeTable, Title = "Descrever tabela da consulta", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "As colunas de uma tabela (tipo, se aceita nulo, padrão, comentário), a chave primária, as únicas, as chaves " +
        "estrangeiras que saem dela e as que chegam de outras tabelas.")]
    public Task<ConsultationTableDetail> DescribeTableAsync(
        [Description("A tabela (ou view).")] string table,
        [Description("O schema; vazio = public.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        gateway.ConsultAsync(
            ConsultationToolNames.DescribeTable,
            (runner, access, token) => runner.RunAsync<DescribeConsultationTableHandler, ConsultationTableDetail>(
                (handler, cancel) => handler.HandleAsync(new DescribeConsultationTable(access.SessionId, schema, table), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = ConsultationToolNames.Query, Title = "Consultar dados", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Uma consulta SELECT (ou WITH … SELECT) no banco desta consulta, só leitura. Uma instrução só; o servidor recusa " +
        "qualquer coisa que altere dados ou estrutura. Volta no máximo 'limit' linhas (padrão 100, teto 500), com tempo " +
        "limite de 15 s; textos longos e binários vêm cortados, e 'truncated' diz quando faltou algo. Filtre e agregue no SQL.")]
    public Task<ConsultationQueryResult> QueryAsync(
        [Description("O SELECT, sem ponto e vírgula no meio.")] string query,
        [Description("Quantas linhas, no máximo (1 a 500; padrão 100).")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        gateway.ConsultAsync(
            ConsultationToolNames.Query,
            (runner, access, token) => runner.RunAsync<QueryConsultationDataHandler, ConsultationQueryResult>(
                (handler, cancel) => handler.HandleAsync(new QueryConsultationData(access.SessionId, query, limit), cancel),
                token),
            cancellationToken);
}
