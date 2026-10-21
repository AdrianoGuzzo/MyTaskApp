using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// Os limites de uma consulta (ADR-081). Os valores padrão são os da tela e do
/// MCP; o teto vale mesmo que alguém peça mais.
/// </summary>
public sealed record ConsultationLimits(
    int MaxRows = ConsultationLimits.DefaultRows,
    int TimeoutSeconds = ConsultationLimits.DefaultTimeoutSeconds,
    int MaxCellLength = 1000,
    int MaxResponseCharacters = 200_000)
{
    public const int DefaultRows = 100;

    public const int RowCeiling = 500;

    public const int DefaultTimeoutSeconds = 15;

    public static ConsultationLimits Default { get; } = new();

    /// <summary>O pedido da IA, preso entre 1 e <see cref="RowCeiling"/>.</summary>
    public ConsultationLimits WithRows(int? rows) =>
        this with { MaxRows = Math.Clamp(rows ?? DefaultRows, 1, RowCeiling) };
}

/// <summary>
/// O usuário da conexão só lê? Respondido pelo catálogo do próprio servidor —
/// não pelo nome do usuário nem pelo que a tela diz.
/// </summary>
/// <param name="Problems">Por que não, em pt-BR; vazio = só leitura.</param>
public sealed record ReadAccessCheck(
    string User,
    string ServerVersion,
    IReadOnlyList<string> Problems)
{
    public bool IsReadOnly => Problems.Count == 0;
}

public sealed record ConsultationDatabaseInfo(
    string Database,
    string ServerVersion,
    IReadOnlyList<string> Schemas);

public sealed record ConsultationTable(
    string Schema,
    string Name,
    string Kind,
    long EstimatedRows,
    string? Comment);

public sealed record ConsultationColumn(
    string Name,
    string Type,
    bool Nullable,
    string? Default,
    string? Comment);

/// <param name="Direction"><c>out</c> = desta tabela para outra; <c>in</c> = de outra para esta.</param>
public sealed record ConsultationConstraint(
    string Name,
    string Kind,
    string Definition,
    string Direction,
    string Table,
    string? ReferencedTable);

public sealed record ConsultationTableDetail(
    string Schema,
    string Name,
    string? Comment,
    IReadOnlyList<ConsultationColumn> Columns,
    IReadOnlyList<ConsultationConstraint> Constraints);

public sealed record ConsultationResultColumn(string Name, string Type);

/// <summary>
/// O resultado de uma consulta, já cortado nos limites. <see cref="Truncated"/>
/// diz que havia mais linhas (ou mais texto) do que o que voltou.
/// </summary>
public sealed record ConsultationQueryResult(
    IReadOnlyList<ConsultationResultColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    long ElapsedMilliseconds);

/// <summary>
/// O banco da Consulta Inteligente (ADR-081): só leitura, com o usuário da
/// conexão. As implementações abrem a sessão com
/// <c>default_transaction_read_only</c> e tempo limite, e cada consulta numa
/// transação só leitura que termina em rollback.
/// </summary>
/// <remarks>
/// Quem escolhe a conexão é o caso de uso, a partir da consulta da tarefa —
/// nenhum método recebe a conexão da IA. Erros voltam como
/// <see cref="Domain.DomainException"/> com o texto do servidor, sem detalhe de
/// linha e sem senha.
/// </remarks>
public interface IConsultationDatabase
{
    Task<ReadAccessCheck> CheckReadAccessAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default);

    Task<ConsultationDatabaseInfo> DescribeAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsultationTable>> ListTablesAsync(
        DatabaseConnectionSnapshot connection,
        string? schema,
        CancellationToken cancellationToken = default);

    Task<ConsultationTableDetail> DescribeTableAsync(
        DatabaseConnectionSnapshot connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uma consulta de leitura. O texto vai para o servidor como subconsulta de
    /// um <c>SELECT … LIMIT $1</c>, pelo protocolo estendido: mais de uma
    /// instrução, DDL, DML, <c>COPY</c>, <c>DO</c> e <c>CALL</c> são recusados
    /// pelo próprio PostgreSQL, e não por uma lista de palavras.
    /// </summary>
    Task<ConsultationQueryResult> QueryAsync(
        DatabaseConnectionSnapshot connection,
        string query,
        ConsultationLimits limits,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// O que "Preparar acesso de leitura" faz no servidor (ADR-081): o usuário
/// <see cref="RoleName"/>, sem nenhum poder além de entrar e ler, com
/// <c>SELECT</c> no banco escolhido.
/// </summary>
public interface IConsultationReadAccessProvisioner
{
    /// <summary>Um por servidor: cópias novas no mesmo servidor só ganham mais um <c>GRANT</c>.</summary>
    public const string RoleName = "mytaskapp_leitura";

    /// <summary>
    /// Pela conexão administrativa <paramref name="admin"/>, já com o banco
    /// escolhido: cria o usuário se não existe, troca a senha quando
    /// <paramref name="password"/> vem (ninguém neste computador sabe a atual)
    /// e dá <c>CONNECT</c>, <c>USAGE</c> e <c>SELECT</c> no banco. Devolve se
    /// o usuário foi criado agora.
    /// </summary>
    Task<bool> PrepareAsync(
        DatabaseConnectionSnapshot admin,
        SecretText? password,
        CancellationToken cancellationToken = default);
}
