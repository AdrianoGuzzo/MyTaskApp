using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>
/// O cofre das senhas das conexões (ADR-056). <b>Só guarda, confere e apaga:</b>
/// nenhum método devolve a senha. Quem a lê é a Infrastructure, na hora de
/// abrir a conexão — a senha nunca volta para a Application nem para a tela.
/// </summary>
public interface IDatabaseCredentialStore
{
    /// <summary>Guarda e devolve a referência para gravar na conexão.</summary>
    Task<string> StoreAsync(Guid connectionId, SecretText password, CancellationToken cancellationToken = default);

    Task<bool> HasAsync(string? reference, CancellationToken cancellationToken = default);

    Task DeleteAsync(string? reference, CancellationToken cancellationToken = default);
}

/// <summary>Acha as ferramentas do PostgreSQL nesta máquina e pergunta a versão de cada uma.</summary>
public interface IPostgresToolLocator
{
    /// <summary><paramref name="refresh"/> = procurar de novo ("Verificar novamente"); sem ele, a última resposta.</summary>
    Task<PostgresClientTools> DetectAsync(bool refresh, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consultas de leitura ao servidor (ADR-056): só SELECT/SHOW, em sessão
/// somente leitura. Nada aqui escreve em banco nenhum, e nenhum método devolve
/// dado de linha — só metadados, contagens e hashes salgados.
/// </summary>
public interface IPostgresServerInspector
{
    /// <summary>
    /// Conecta e diz o que viu. Nunca lança por falha de conexão: devolve
    /// <see cref="ServerDiagnostics.Connected"/> falso com a mensagem.
    /// <paramref name="password"/> serve para testar antes de salvar; sem ela, vale a guardada.
    /// </summary>
    Task<ServerDiagnostics> TestAsync(
        DatabaseConnectionSnapshot connection,
        SecretText? password = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TableInfo>> ListTablesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default);

    Task<DatabaseStructure> GetStructureAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default);

    /// <summary>Exato até <paramref name="exactLimit"/> linhas estimadas; acima disso, a estimativa do catálogo.</summary>
    Task<IReadOnlyList<RowCount>> CountRowsAsync(
        DatabaseConnectionSnapshot connection,
        IReadOnlyList<string> tables,
        long exactLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Os hashes salgados de uma coluna, pela chave primária. Tabela sem chave
    /// primária devolve vazio: sem chave não há como parear origem e destino.
    /// </summary>
    Task<ColumnFingerprint> FingerprintAsync(
        DatabaseConnectionSnapshot connection,
        ColumnReference column,
        string salt,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>O PostgreSQL Anonymizer no servidor, pela leitura do catálogo. Nunca chama função que altere dado.</summary>
public interface IPostgresAnonymizerInspector
{
    Task<AnonymizerStatus> GetStatusAsync(
        DatabaseConnectionSnapshot connection,
        string policyName,
        CancellationToken cancellationToken = default);
}

/// <summary>Um dump. <see cref="Anonymous"/> = feito pela role mascarada, com as opções que tiram o anon do arquivo.</summary>
public sealed record PgDumpRequest(
    DatabaseConnectionSnapshot Connection,
    string OutputDirectory,
    bool Anonymous,
    bool IncludeSchema,
    bool IncludeData,
    int Jobs,
    IReadOnlyCollection<string> ProtectedEndpoints);

/// <summary>Um restore num banco que já existe.</summary>
public sealed record PgRestoreRequest(
    DatabaseConnectionSnapshot Target,
    string ArchiveDirectory,
    bool IncludeSchema,
    bool IncludeData,
    int Jobs,
    IReadOnlyCollection<string> ProtectedEndpoints);

/// <summary>
/// <c>pg_dump</c> atrás de uma porta (ADR-056). A implementação confere a
/// política de novo logo antes do processo, e o processo roda sem senha nos
/// argumentos.
/// </summary>
public interface IPostgresDumpService
{
    Task<PgToolRun> DumpAsync(PgDumpRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default);

    /// <summary>O índice do dump, sem restaurar nada (<c>pg_restore --list</c>).</summary>
    Task<ArchiveSummary> ListArchiveAsync(string archiveDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>pg_restore</c>, <c>createdb</c> e <c>dropdb</c> atrás de uma porta
/// (ADR-056). Toda chamada é recusada antes do processo quando o alvo é
/// produção, ou aponta para o banco de uma conexão de produção.
/// </summary>
public interface IPostgresRestoreService
{
    Task<PgToolRun> RestoreAsync(PgRestoreRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default);

    Task<PgToolRun> CreateDatabaseAsync(
        DatabaseConnectionSnapshot target,
        IReadOnlyCollection<string> protectedEndpoints,
        CancellationToken cancellationToken = default);

    Task<PgToolRun> DropDatabaseAsync(
        DatabaseConnectionSnapshot target,
        IReadOnlyCollection<string> protectedEndpoints,
        bool force,
        CancellationToken cancellationToken = default);
}

/// <summary>O que fica de uma operação depois da limpeza: só metadados.</summary>
public sealed record DatabaseOperationMetadata(
    Guid OperationId,
    DatabaseOperationType Operation,
    DatabaseOperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Source,
    string? Destination,
    string? AnonymizationProfile,
    string? ToolVersions,
    long? AnonymousDumpSize,
    int? MaskedColumns,
    IReadOnlyList<string> Steps,
    bool KeptAnonymizedArtifact);

/// <summary>
/// A pasta isolada de uma operação: <c>dump/</c>, <c>anonymized/</c>,
/// <c>logs/</c> e <c>metadata.json</c>. Só o dono lê; no fim, sobra o metadado.
/// </summary>
public interface IDatabaseOperationWorkspace
{
    string Root { get; }

    /// <summary>Dump sem anonimização. Num fluxo de produção fica vazio: esse dump não existe.</summary>
    string DumpDirectory { get; }

    string AnonymizedDirectory { get; }

    string LogsDirectory { get; }

    /// <summary>Bytes de um arquivo ou de uma pasta inteira; 0 se não existir.</summary>
    long SizeOf(string path);

    /// <summary>Espaço livre no disco da pasta; <c>null</c> quando o sistema não diz.</summary>
    long? AvailableBytes();

    Task WriteMetadataAsync(DatabaseOperationMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Apaga o que é sensível. Devolve onde ficou o dump anônimo, se foi mantido.</summary>
    Task<string?> CleanupAsync(bool keepAnonymized, CancellationToken cancellationToken = default);
}

public interface IDatabaseOperationWorkspaceFactory
{
    Task<IDatabaseOperationWorkspace> CreateAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Apaga dumps de operações que não terminaram a limpeza (o app caiu no meio).</summary>
    Task<int> SweepAsync(IReadOnlyCollection<Guid> liveOperations, CancellationToken cancellationToken = default);
}
