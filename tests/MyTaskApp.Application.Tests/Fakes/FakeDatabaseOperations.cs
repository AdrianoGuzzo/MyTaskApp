using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>O cadastro em memória, com uma unidade de trabalho que conta as gravações.</summary>
internal sealed class FakeDatabaseCatalog : IUnitOfWork
{
    public FakeConnectionRepository Connections { get; }

    public FakeAnonymizationProfileRepository AnonymizationProfiles { get; } = new();

    public FakeCopyProfileRepository CopyProfiles { get; } = new();

    public FakeSavedDatabaseRepository SavedDatabases { get; } = new();

    public FakeDatabaseAuditLog Audit { get; } = new();

    public FakeDatabaseCatalog() => Connections = new FakeConnectionRepository();

    public int SaveCount { get; private set; }

    /// <summary>O estado de cada auditoria a cada gravação: prova que "em andamento" foi gravado antes.</summary>
    public List<(Guid Id, DatabaseOperationStatus Status)> SavedAuditStates { get; } = [];

    public Exception? SaveFailure { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        SaveCount++;
        SavedAuditStates.AddRange(Audit.Entries.Select(entry => (entry.Id, entry.Status)));
        return Task.CompletedTask;
    }
}

internal sealed class FakeConnectionRepository : IDatabaseConnectionRepository
{
    public List<DatabaseConnection> Items { get; } = [];

    public void Seed(params DatabaseConnection[] connections) => Items.AddRange(connections);

    public Task AddAsync(DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        Items.Add(connection);
        return Task.CompletedTask;
    }

    public Task<DatabaseConnection?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(connection => connection.Id == id));

    public Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseConnection>>(Items.OrderBy(connection => connection.Name).ToList());

    public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(connection =>
            string.Equals(connection.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && connection.Id != exceptId));

    public void Remove(DatabaseConnection connection) => Items.Remove(connection);
}

internal sealed class FakeAnonymizationProfileRepository : IAnonymizationProfileRepository
{
    public List<AnonymizationProfile> Items { get; } = [];

    public Task AddAsync(AnonymizationProfile profile, CancellationToken cancellationToken = default)
    {
        Items.Add(profile);
        return Task.CompletedTask;
    }

    public Task<AnonymizationProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(profile => profile.Id == id));

    public Task<IReadOnlyList<AnonymizationProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AnonymizationProfile>>(Items.ToList());

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(profile => profile.ConnectionId == connectionId));

    public void Remove(AnonymizationProfile profile) => Items.Remove(profile);
}

internal sealed class FakeCopyProfileRepository : IDatabaseCopyProfileRepository
{
    public List<DatabaseCopyProfile> Items { get; } = [];

    public Task AddAsync(DatabaseCopyProfile profile, CancellationToken cancellationToken = default)
    {
        Items.Add(profile);
        return Task.CompletedTask;
    }

    public Task<DatabaseCopyProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(profile => profile.Id == id));

    public Task<IReadOnlyList<DatabaseCopyProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseCopyProfile>>(Items.ToList());

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(profile => profile.SourceConnectionId == connectionId || profile.DestinationConnectionId == connectionId));

    public Task<bool> AnyUsesAnonymizationProfileAsync(Guid anonymizationProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(profile => profile.AnonymizationProfileId == anonymizationProfileId));

    public void Remove(DatabaseCopyProfile profile) => Items.Remove(profile);
}

internal sealed class FakeSavedDatabaseRepository : ISavedDatabaseRepository
{
    public List<SavedDatabase> Items { get; } = [];

    public Task AddAsync(SavedDatabase saved, CancellationToken cancellationToken = default)
    {
        Items.Add(saved);
        return Task.CompletedTask;
    }

    public Task<SavedDatabase?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(saved => saved.Id == id));

    public Task<IReadOnlyList<SavedDatabase>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedDatabase>>(Items.OrderBy(saved => saved.Alias, StringComparer.Ordinal).ToList());

    public Task<bool> AliasExistsAsync(string alias, Guid? exceptId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(saved =>
            string.Equals(saved.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase) && saved.Id != exceptId));

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(saved => saved.ConnectionId == connectionId));

    public Task<bool> AnyUsesAnonymizationProfileAsync(Guid anonymizationProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.Any(saved => saved.AnonymizationProfileId == anonymizationProfileId));

    public void Remove(SavedDatabase saved) => Items.Remove(saved);
}

internal sealed class FakeDatabaseAuditLog : IDatabaseOperationAuditLog
{
    public List<DatabaseOperationAudit> Entries { get; } = [];

    public Task RecordAsync(DatabaseOperationAudit audit, CancellationToken cancellationToken = default)
    {
        Entries.Add(audit);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DatabaseOperationAudit>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseOperationAudit>>(Entries.OrderByDescending(entry => entry.StartedAt).Take(limit).ToList());

    public Task<IReadOnlyList<DatabaseOperationAudit>> ListRunningAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseOperationAudit>>(Entries.Where(entry => entry.IsRunning).ToList());
}

/// <summary>O cofre: guarda em memória e conta quem pediu o quê. Nunca devolve a senha — como o de verdade.</summary>
internal sealed class FakeCredentialStore : IDatabaseCredentialStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public Exception? StoreFailure { get; set; }

    public Task<string> StoreAsync(Guid connectionId, SecretText password, CancellationToken cancellationToken = default)
    {
        if (StoreFailure is not null)
        {
            throw StoreFailure;
        }

        var reference = DatabaseConnection.SecretReferencePrefix + connectionId.ToString("N");
        Secrets[reference] = password.Reveal();
        return Task.FromResult(reference);
    }

    public Task<bool> HasAsync(string? reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(reference is not null && Secrets.ContainsKey(reference));

    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (reference is not null)
        {
            Secrets.Remove(reference);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeToolLocator : IPostgresToolLocator
{
    public static PostgresInstallGuide Guide { get; } = new("Windows", ["winget install PostgreSQL.PostgreSQL.17"], ["where.exe pg_dump"]);

    public PostgresClientTools Tools { get; set; } = WithVersion(17, 2);

    public int Detections { get; private set; }

    public bool LastRefresh { get; private set; }

    public static PostgresClientTools WithVersion(int major, int minor, params PostgresTool[] missing) => new(
        Enum.GetValues<PostgresTool>()
            .Select(tool => missing.Contains(tool)
                ? new PostgresToolStatus(tool, PostgresToolNames.Of(tool), null, null, null)
                : new PostgresToolStatus(tool, PostgresToolNames.Of(tool), $@"C:\Program Files\PostgreSQL\{major}\bin\{PostgresToolNames.Of(tool)}.exe",
                    new PostgresVersion(major, minor), $"{PostgresToolNames.Of(tool)} (PostgreSQL) {major}.{minor}"))
            .ToList(),
        Guide);

    public Task<PostgresClientTools> DetectAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        Detections++;
        LastRefresh = refresh;
        return Task.FromResult(Tools);
    }
}

/// <summary>
/// O servidor, por conexão: diagnóstico, tabelas, estrutura, contagens e os
/// hashes salgados. Por padrão, tudo igual nos dois lados e as colunas
/// mascaradas diferentes — a cópia que deu certo.
/// </summary>
internal sealed class FakeServerInspector : IPostgresServerInspector
{
    public Dictionary<string, ServerDiagnostics> Diagnostics { get; } = [];

    public List<(DatabaseConnectionSnapshot Connection, SecretText? Password)> Tests { get; } = [];

    public List<string> Calls { get; } = [];

    public IReadOnlyList<TableInfo> Tables { get; set; } =
    [
        new("public", "clientes", 8_000_000, 1000),
        new("public", "pedidos", 2_000_000, 5000),
    ];

    /// <summary>Linhas de uma tabela numa conexão (pelo nome); sem resposta, 10 dos dois lados.</summary>
    public Func<string, string, long?>? RowsOf { get; set; }

    public IReadOnlyList<ColumnInfo> Columns { get; set; } =
    [
        new("public", "clientes", "id", "integer", null),
        new("public", "clientes", "email", "text", null),
        new("public", "clientes", "cpf", "character varying", null),
        new("public", "pedidos", "valor", "numeric", null),
    ];

    public DatabaseStructure Structure { get; set; } = new(
        ["public"],
        ["public.clientes", "public.pedidos"],
        new Dictionary<string, int> { ["p"] = 2, ["f"] = 1 },
        3,
        2);

    /// <summary>A estrutura do destino, quando diferente da origem.</summary>
    public DatabaseStructure? DestinationStructure { get; set; }

    public Guid? DestinationId { get; set; }

    /// <summary>Quem devolve os valores iguais aos reais: a conexão cujo id estiver aqui "não mascara".</summary>
    public HashSet<Guid> Unmasked { get; } = [];

    public Exception? Failure { get; set; }

    public static ServerDiagnostics Connected(string version = "17.4") => new(
        true,
        null,
        $"PostgreSQL {version} on x86_64",
        PostgresVersion.TryParse(version),
        "backup_user",
        "eco_core",
        10_000_000,
        ["public"],
        2,
        new ServerPrivileges(false, false, true, 0, -1));

    public Task<ServerDiagnostics> TestAsync(DatabaseConnectionSnapshot connection, SecretText? password = null, CancellationToken cancellationToken = default)
    {
        Tests.Add((connection, password));
        Calls.Add($"test:{connection.Name}:{connection.Database}");
        ThrowIfFailing();
        return Task.FromResult(Diagnostics.TryGetValue(connection.Name, out var result) ? result : Connected());
    }

    /// <summary>Os bancos do servidor, para a escolha numa conexão só de servidor (ADR-057).</summary>
    public IReadOnlyList<string> Databases { get; set; } = ["eco_core_1010", "eco_core_2020"];

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add($"databases:{connection.Name}");
        ThrowIfFailing();
        return Task.FromResult(Databases);
    }

    public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add($"columns:{connection.Name}:{connection.Database}");
        ThrowIfFailing();
        return Task.FromResult(Columns);
    }

    public Task<IReadOnlyList<TableInfo>> ListTablesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add($"tables:{connection.Name}");
        return Task.FromResult(Tables);
    }

    public Task<DatabaseStructure> GetStructureAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add($"structure:{connection.Name}");
        return Task.FromResult(connection.Id == DestinationId && DestinationStructure is not null ? DestinationStructure : Structure);
    }

    public Task<IReadOnlyList<RowCount>> CountRowsAsync(
        DatabaseConnectionSnapshot connection,
        IReadOnlyList<string> tables,
        long exactLimit,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"rows:{connection.Name}");
        return Task.FromResult<IReadOnlyList<RowCount>>(tables
            .Select(table => new RowCount(table, RowsOf?.Invoke(connection.Name, table) ?? 10, false))
            .ToList());
    }

    public Task<ColumnFingerprint> FingerprintAsync(
        DatabaseConnectionSnapshot connection,
        ColumnReference column,
        string salt,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"fingerprint:{connection.Name}:{column.ColumnKey}");

        // A origem (e quem não mascara) devolve o "real"; os outros, valores trocados.
        var real = connection.Environment is DatabaseEnvironment.Production or DatabaseEnvironment.CriticalProduction
            && connection.Username != "dump_anon";
        var values = Enumerable.Range(1, 5).ToDictionary(
            index => $"pk{index}",
            index => (string?)(real || Unmasked.Contains(connection.Id) ? $"v{index}" : $"m{index}"));

        return Task.FromResult(new ColumnFingerprint(column, values));
    }

    /// <summary>Linhas sem a máscara fixa, por coluna; o padrão é zero — a cópia que deu certo.</summary>
    public Dictionary<string, long> NotMasked { get; } = [];

    public Task<long> CountNotMaskedAsync(
        DatabaseConnectionSnapshot connection,
        ColumnReference column,
        MaskedColumnPlan mask,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"notmasked:{connection.Name}:{column.ColumnKey}");
        return Task.FromResult(NotMasked.GetValueOrDefault(column.ColumnKey));
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>
/// A cópia mascarada sem servidor (ADR-058): o catálogo de duas tabelas, e a
/// sessão que anota cada passo na mesma lista das ferramentas — para a ordem
/// entre <c>pg_dump</c>, COPY e <c>pg_restore</c> aparecer junta.
/// </summary>
internal sealed class FakeMaskedCopier(List<string> calls) : IPostgresMaskedCopier
{
    public const string Snapshot = "00000003-0000001B-1";

    public SourceCatalog Catalog { get; set; } = DefaultCatalog();

    /// <summary>A mesma lista das ferramentas.</summary>
    public List<string> Calls => calls;

    public List<MaskedTablePlan> Copied { get; } = [];

    public List<(string Table, IReadOnlyList<MaskedTablePlan> Tables, int Rows)> Previews { get; } = [];

    /// <summary>Lançada ao copiar esta tabela.</summary>
    public (string Table, Exception Failure)? CopyFailure { get; set; }

    /// <summary>Chamado no meio da cópia — para cancelar enquanto ela roda.</summary>
    public Action? DuringCopy { get; set; }

    public DatabaseConnectionSnapshot? Destination { get; private set; }

    public bool Closed { get; private set; }

    public static SourceCatalog DefaultCatalog() => new(
        [
            new SourceTable("public", "clientes", 8_000_000, 1000,
            [
                new SourceColumn("id", "integer", false, false),
                new SourceColumn("email", "text", true, false),
                new SourceColumn("cpf", "character varying(14)", true, false),
                new SourceColumn("nome_busca", "text", true, true),
            ]),
            new SourceTable("public", "pedidos", 2_000_000, 5000,
            [
                new SourceColumn("id", "integer", false, false),
                new SourceColumn("cliente_id", "integer", false, false),
                new SourceColumn("valor", "numeric", false, false),
            ]),
        ],
        [
            new KeyColumn("public", "clientes", "id", KeyRole.Primary),
            new KeyColumn("public", "clientes", "id", KeyRole.Referenced),
            new KeyColumn("public", "pedidos", "id", KeyRole.Primary),
            new KeyColumn("public", "pedidos", "cliente_id", KeyRole.Foreign),
        ],
        [
            new ColumnInfo("public", "clientes", "id", "integer", null),
            new ColumnInfo("public", "clientes", "email", "text", null),
            new ColumnInfo("public", "clientes", "cpf", "character varying", null),
            new ColumnInfo("public", "pedidos", "valor", "numeric", null),
        ],
        0);

    public Task<SourceCatalog> ReadCatalogAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default)
    {
        calls.Add($"catalog:{source.Name}:{source.Database}");
        return Task.FromResult(Catalog);
    }

    public Task<IReadOnlyList<MaskedPreview>> PreviewAsync(
        DatabaseConnectionSnapshot source,
        IReadOnlyList<MaskedTablePlan> tables,
        int rows,
        CancellationToken cancellationToken = default)
    {
        Previews.Add((source.Name, tables, rows));

        return Task.FromResult<IReadOnlyList<MaskedPreview>>(tables
            .SelectMany(table => table.Columns.Where(column => column.IsMasked)
                .Select(column => new MaskedPreview(table.Schema, table.Table, column.Name,
                    Enumerable.Range(1, rows).Select(index => (string?)$"m{index}").ToList())))
            .ToList());
    }

    public Task<IMaskedCopySession> OpenAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default)
    {
        calls.Add($"open:{source.Name}:{source.Database}");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMaskedCopySession>(new Session(this));
    }

    private sealed class Session(FakeMaskedCopier owner) : IMaskedCopySession
    {
        public string SnapshotId => Snapshot;

        public Task ConnectDestinationAsync(
            DatabaseConnectionSnapshot destination,
            IReadOnlyCollection<string> protectedEndpoints,
            CancellationToken cancellationToken = default)
        {
            owner.Calls.Add($"connect:{destination.Database}");
            owner.Destination = destination;
            return Task.CompletedTask;
        }

        public Task<long> CopyTableAsync(MaskedTablePlan table, Action<long>? rows, CancellationToken cancellationToken = default)
        {
            owner.Calls.Add($"copy:{table.QualifiedName}");
            owner.Copied.Add(table);
            owner.DuringCopy?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            if (owner.CopyFailure is { } failure && failure.Table == table.QualifiedName)
            {
                throw failure.Failure;
            }

            rows?.Invoke(5);
            rows?.Invoke(10);
            return Task.FromResult(10L);
        }

        public Task<int> CopySequencesAsync(CancellationToken cancellationToken = default)
        {
            owner.Calls.Add("sequences");
            return Task.FromResult(2);
        }

        public ValueTask DisposeAsync()
        {
            owner.Calls.Add("close");
            owner.Closed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>pg_dump/pg_restore/createdb/dropdb sem processo: anota a ordem e responde o roteiro.</summary>
internal sealed class FakePgTools : IPostgresDumpService, IPostgresRestoreService
{
    public List<string> Calls { get; } = [];

    public List<PgDumpRequest> Dumps { get; } = [];

    public List<PgRestoreRequest> Restores { get; } = [];

    public PgToolRun DumpResult { get; set; } = Ok();

    public PgToolRun RestoreResult { get; set; } = Ok();

    public PgToolRun DropResult { get; set; } = Ok();

    public PgToolRun CreateResult { get; set; } = Ok();

    public ArchiveSummary Archive { get; set; } = new(2, 3, 3, ["public.clientes", "public.pedidos"]);

    /// <summary>O índice do dump só da estrutura: sem dados. Trocar para simular um dump que trouxe linha.</summary>
    public ArchiveSummary SchemaArchive { get; set; } = new(0, 3, 3, []);

    private bool _schemaOnly;

    public bool DropForced { get; private set; }

    /// <summary>Chamado no meio do restore — para cancelar enquanto ele roda.</summary>
    public Action? DuringRestore { get; set; }

    public static PgToolRun Ok() => new(0, false, TimeSpan.FromSeconds(3), string.Empty);

    public Task<PgToolRun> DumpAsync(PgDumpRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add(request.SchemaOnly ? "schema-dump" : "dump");
        Dumps.Add(request);
        _schemaOnly = request.SchemaOnly;
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_dump: dumping contents of table \"public.clientes\"", true), PgToolEventKind.TableData, "public.clientes"));
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_dump: dumping contents of table \"public.pedidos\"", true), PgToolEventKind.TableData, "public.pedidos"));
        return Task.FromResult(DumpResult);
    }

    public Task<ArchiveSummary> ListArchiveAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        Calls.Add("list");
        return Task.FromResult(_schemaOnly ? SchemaArchive : Archive);
    }

    public Task<PgToolRun> RestoreAsync(PgRestoreRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add(request.Section switch
        {
            RestoreSection.PreData => "restore:pre-data",
            RestoreSection.PostData => "restore:post-data",
            _ => "restore",
        });
        Restores.Add(request);
        DuringRestore?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_restore: processing data for table \"public.clientes\"", true), PgToolEventKind.TableData, "public.clientes"));
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_restore: creating INDEX \"public.ix\"", true), PgToolEventKind.PostData));
        return Task.FromResult(RestoreResult);
    }

    public Task<PgToolRun> CreateDatabaseAsync(DatabaseConnectionSnapshot target, IReadOnlyCollection<string> protectedEndpoints, CancellationToken cancellationToken = default)
    {
        Calls.Add($"createdb:{target.Database}");
        return Task.FromResult(CreateResult);
    }

    public Task<PgToolRun> DropDatabaseAsync(DatabaseConnectionSnapshot target, IReadOnlyCollection<string> protectedEndpoints, bool force, CancellationToken cancellationToken = default)
    {
        Calls.Add($"dropdb:{target.Database}");
        DropForced = force;
        return Task.FromResult(DropResult);
    }
}

internal sealed class FakeWorkspaceFactory : IDatabaseOperationWorkspaceFactory
{
    public List<FakeWorkspace> Created { get; } = [];

    public List<IReadOnlyCollection<Guid>> Sweeps { get; } = [];

    public int SweepResult { get; set; }

    public long? AvailableBytes { get; set; } = long.MaxValue;

    public Task<IDatabaseOperationWorkspace> CreateAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var workspace = new FakeWorkspace(operationId, AvailableBytes);
        Created.Add(workspace);
        return Task.FromResult<IDatabaseOperationWorkspace>(workspace);
    }

    public Task<int> SweepAsync(IReadOnlyCollection<Guid> liveOperations, CancellationToken cancellationToken = default)
    {
        Sweeps.Add(liveOperations);
        return Task.FromResult(SweepResult);
    }
}

internal sealed class FakeWorkspace(Guid operationId, long? available) : IDatabaseOperationWorkspace
{
    public string Root { get; } = Path.Combine("ws", $"operation-{operationId:N}");

    public string DumpDirectory => Path.Combine(Root, "dump");

    public string AnonymizedDirectory => Path.Combine(Root, "anonymized");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public bool? CleanedKeepingAnonymized { get; private set; }

    public DatabaseOperationMetadata? Metadata { get; private set; }

    public long SizeOf(string path) => 4096;

    public long? AvailableBytes() => available;

    public Task WriteMetadataAsync(DatabaseOperationMetadata metadata, CancellationToken cancellationToken = default)
    {
        Metadata = metadata;
        return Task.CompletedTask;
    }

    public Task<string?> CleanupAsync(bool keepAnonymized, CancellationToken cancellationToken = default)
    {
        CleanedKeepingAnonymized = keepAnonymized;
        return Task.FromResult<string?>(keepAnonymized ? AnonymizedDirectory : null);
    }
}

/// <summary>Ambiente completo de uma cópia ECO Produção → ECO Desenvolvimento, pronto para quebrar uma peça por teste.</summary>
internal sealed class DatabaseCopyScenario
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    public FakeDatabaseCatalog Catalog { get; } = new();

    public FakeCredentialStore Credentials { get; } = new();

    public FakeToolLocator Locator { get; } = new();

    public FakeServerInspector Inspector { get; } = new();

    public FakePgTools Tools { get; } = new();

    public FakeMaskedCopier Copier { get; }

    public FakeWorkspaceFactory Workspaces { get; } = new();

    public DatabaseOperationGate Gate { get; } = new();

    public DatabaseSecurityPolicy Policy { get; } = new();

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock { get; } = new(Now);

    public DatabaseConnection Production { get; }

    public DatabaseConnection Development { get; }

    public AnonymizationProfile Profile { get; }

    public DatabaseCopyScenario(DatabaseEnvironment sourceEnvironment = DatabaseEnvironment.Production)
    {
        var everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);
        Copier = new FakeMaskedCopier(Tools.Calls);
        Production = DatabaseConnection.Create("ECO Produção", "192.168.15.112", 5432, "eco_core", "backup_user",
            sourceEnvironment, DatabaseSslMode.Prefer, null, everything, Now);
        Development = DatabaseConnection.Create("ECO Desenvolvimento", "localhost", 5432, "eco_dev", "postgres",
            sourceEnvironment == DatabaseEnvironment.CriticalProduction ? DatabaseEnvironment.Test : DatabaseEnvironment.Development,
            DatabaseSslMode.Prefer, null, everything with { RequireAnonymization = false }, Now);
        Catalog.Connections.Seed(Production, Development);

        Profile = AnonymizationProfile.Create("ECO LGPD", null, Production.Id, Now);
        Profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingMethod.Partial, "0,2", ColumnSensitivity.High),
        ], Now);
        Catalog.AnonymizationProfiles.Items.Add(Profile);
        Inspector.DestinationId = Development.Id;
    }

    public DatabaseCopyRequest Request(DatabaseCopyOptions? options = null, DatabaseOperationType operation = DatabaseOperationType.CopyAndAnonymize) =>
        new(Production.Id, Development.Id, operation, Profile.Id, options ?? DatabaseCopyOptions.Default);

    public DatabaseCopyPlanner Planner() =>
        new(Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases, Clock);

    public MaskingVerifier Verifier() => new(Inspector, Policy);

    public RunDatabaseCopyHandler RunHandler() => new(
        Planner(),
        Policy,
        Locator,
        Inspector,
        Copier,
        Verifier(),
        Tools,
        Tools,
        Workspaces,
        Catalog.Audit,
        Catalog,
        Gate,
        new FakeCurrentUser(),
        Clock,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDatabaseCopyHandler>.Instance);

    public ValidateDatabaseCopyHandler ValidateHandler() => new(Planner(), Policy, Locator, Inspector, Copier);
}

/// <summary>Junta os avisos de progresso numa lista, na mesma thread.</summary>
internal sealed class ProgressLog<T> : IProgress<T>
{
    public List<T> Items { get; } = [];

    public void Report(T value) => Items.Add(value);
}

/// <summary>Uma falha de domínio pronta para os fakes lançarem.</summary>
internal static class Failures
{
    public static DomainException Domain(string message = "falhou") => new(message);
}
