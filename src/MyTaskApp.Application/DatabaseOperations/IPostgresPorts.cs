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

    /// <summary>
    /// Os bancos do servidor em que se pode conectar, por nome (ADR-057), sem os
    /// templates nem o <c>postgres</c>. Lido pelo banco de manutenção, então
    /// serve para uma conexão que é só o servidor.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnectionSnapshot connection,
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

    /// <summary>
    /// Quantas linhas de uma coluna com máscara fixa (texto, número, nulo) não
    /// têm o valor da máscara. Depois da cópia, tem de ser zero.
    /// </summary>
    Task<long> CountNotMaskedAsync(
        DatabaseConnectionSnapshot connection,
        ColumnReference column,
        MaskedColumnPlan mask,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Um dump. Só a estrutura (<see cref="IncludeData"/> falso) pode sair de quem
/// exige anonimização: nenhuma linha vai junto. <see cref="Snapshot"/> é a foto
/// exportada pela sessão da cópia mascarada (ADR-058): estrutura e dados do mesmo instante.
/// <see cref="SourceVersion"/> escolhe o conjunto de ferramentas (<see cref="PostgresClientTools.ForSource"/>).
/// </summary>
public sealed record PgDumpRequest(
    DatabaseConnectionSnapshot Connection,
    string OutputDirectory,
    bool IncludeSchema,
    bool IncludeData,
    int Jobs,
    IReadOnlyCollection<string> ProtectedEndpoints,
    string? Snapshot = null,
    PostgresVersion? SourceVersion = null)
{
    public bool SchemaOnly => IncludeSchema && !IncludeData;
}

/// <summary>A parte de um dump restaurada sozinha (<c>--section</c>).</summary>
public enum RestoreSection
{
    /// <summary>Tipos, tabelas, sequences e funções: o que precisa existir antes dos dados.</summary>
    PreData,

    /// <summary>Índices, constraints, FKs e triggers: depois dos dados, para não pesar no COPY.</summary>
    PostData,
}

/// <summary>
/// Um restore num banco que já existe. <see cref="Section"/> restaura só aquela parte.
/// <see cref="SourceVersion"/> é a da origem do dump: o restore usa o mesmo conjunto do <c>pg_dump</c>.
/// </summary>
public sealed record PgRestoreRequest(
    DatabaseConnectionSnapshot Target,
    string ArchiveDirectory,
    bool IncludeSchema,
    bool IncludeData,
    int Jobs,
    IReadOnlyCollection<string> ProtectedEndpoints,
    RestoreSection? Section = null,
    PostgresVersion? SourceVersion = null);

/// <summary>
/// A cópia mascarada na consulta (ADR-058). Nada é instalado na origem: o app
/// lê o catálogo, monta um <c>COPY (SELECT …) TO STDOUT</c> com as máscaras
/// e grava o resultado no destino por <c>COPY … FROM STDIN</c>. O dado real
/// não sai do servidor de origem — nem para o disco, nem para a memória do app.
/// </summary>
public interface IPostgresMaskedCopier
{
    /// <summary>Tabelas, colunas, chaves e objetos grandes da origem — só catálogo, nenhuma linha.</summary>
    Task<SourceCatalog> ReadCatalogAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default);

    /// <summary>Algumas linhas de cada tabela, já mascaradas no servidor: só as colunas com regra, nunca o valor real.</summary>
    Task<IReadOnlyList<MaskedPreview>> PreviewAsync(
        DatabaseConnectionSnapshot source,
        IReadOnlyList<MaskedTablePlan> tables,
        int rows,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre a leitura na origem: somente leitura, uma transação só, com o
    /// snapshot exportado para o <c>pg_dump</c> da estrutura ler a mesma foto.
    /// </summary>
    Task<IMaskedCopySession> OpenAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default);
}

/// <summary>Uma cópia mascarada em andamento: a mesma foto da origem do começo ao fim.</summary>
public interface IMaskedCopySession : IAsyncDisposable
{
    /// <summary>O id de <c>pg_export_snapshot()</c>, para o <c>pg_dump --snapshot</c> da estrutura.</summary>
    string SnapshotId { get; }

    /// <summary>
    /// Abre a escrita no destino — depois de ele existir. Passa pela mesma
    /// guarda dos processos: produção, ou um banco de servidor de produção, nunca recebe nada.
    /// </summary>
    Task ConnectDestinationAsync(
        DatabaseConnectionSnapshot destination,
        IReadOnlyCollection<string> protectedEndpoints,
        CancellationToken cancellationToken = default);

    /// <summary>Copia uma tabela; <paramref name="rows"/> recebe o total de linhas a cada bloco.</summary>
    Task<long> CopyTableAsync(MaskedTablePlan table, Action<long>? rows, CancellationToken cancellationToken = default);

    /// <summary>Leva o valor atual de cada sequence da origem para o destino (<c>setval</c>).</summary>
    Task<int> CopySequencesAsync(CancellationToken cancellationToken = default);
}

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
