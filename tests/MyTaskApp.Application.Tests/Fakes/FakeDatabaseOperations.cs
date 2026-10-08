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

    public static ServerDiagnostics Connected(string version = "16.4") => new(
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

    public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add($"columns:{connection.Name}");
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
        return Task.FromResult<IReadOnlyList<RowCount>>(tables.Select(table => new RowCount(table, 10, false)).ToList());
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

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

internal sealed class FakeAnonymizerInspector : IPostgresAnonymizerInspector
{
    public AnonymizerStatus Status { get; set; } = Healthy();

    public static AnonymizerStatus Healthy(params ServerMaskingRule[] rules) => new(
        true,
        "2.1.0",
        true,
        "2.1.0",
        true,
        true,
        true,
        rules.Length > 0
            ? rules
            :
            [
                new ServerMaskingRule("public", "clientes", "email", "MASKED WITH FUNCTION anon.partial_email(email)"),
                new ServerMaskingRule("public", "clientes", "cpf", "MASKED WITH FUNCTION anon.partial(cpf,0,$$*********$$,2)"),
            ]);

    public Task<AnonymizerStatus> GetStatusAsync(DatabaseConnectionSnapshot connection, string policyName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);
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

    public ArchiveSummary Archive { get; set; } = new(2, 0, false, 3, 3, ["public.clientes", "public.pedidos"]);

    public bool DropForced { get; private set; }

    /// <summary>Chamado no meio do restore — para cancelar enquanto ele roda.</summary>
    public Action? DuringRestore { get; set; }

    public static PgToolRun Ok() => new(0, false, TimeSpan.FromSeconds(3), string.Empty);

    public Task<PgToolRun> DumpAsync(PgDumpRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add(request.Anonymous ? "anonymous-dump" : "dump");
        Dumps.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_dump: dumping contents of table \"public.clientes\"", true), PgToolEventKind.TableData, "public.clientes"));
        progress?.Report(new PgToolEvent(new CommandOutputLine("pg_dump: dumping contents of table \"public.pedidos\"", true), PgToolEventKind.TableData, "public.pedidos"));
        return Task.FromResult(DumpResult);
    }

    public Task<ArchiveSummary> ListArchiveAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        Calls.Add("list");
        return Task.FromResult(Archive);
    }

    public Task<PgToolRun> RestoreAsync(PgRestoreRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add("restore");
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

    public FakeAnonymizerInspector Anonymizer { get; } = new();

    public FakePgTools Tools { get; } = new();

    public FakeWorkspaceFactory Workspaces { get; } = new();

    public DatabaseOperationGate Gate { get; } = new();

    public DatabaseSecurityPolicy Policy { get; } = new();

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock { get; } = new(Now);

    public DatabaseConnection Production { get; }

    public DatabaseConnection Masked { get; }

    public DatabaseConnection Development { get; }

    public AnonymizationProfile Profile { get; }

    public DatabaseCopyScenario(DatabaseEnvironment sourceEnvironment = DatabaseEnvironment.Production)
    {
        var everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);
        Production = DatabaseConnection.Create("ECO Produção", "192.168.15.112", 5432, "eco_core", "backup_user",
            sourceEnvironment, DatabaseSslMode.Prefer, null, everything, Now);
        Masked = DatabaseConnection.Create("ECO Produção (anon)", "192.168.15.112", 5432, "eco_core", "dump_anon",
            sourceEnvironment, DatabaseSslMode.Prefer, null, everything, Now);
        Development = DatabaseConnection.Create("ECO Desenvolvimento", "localhost", 5432, "eco_dev", "postgres",
            sourceEnvironment == DatabaseEnvironment.CriticalProduction ? DatabaseEnvironment.Test : DatabaseEnvironment.Development,
            DatabaseSslMode.Prefer, null, everything with { RequireAnonymization = false }, Now);
        Catalog.Connections.Seed(Production, Masked, Development);

        Profile = AnonymizationProfile.Create("ECO LGPD", null, Masked.Id, "anon", Now);
        Profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingKind.Function, "anon.partial_email(email)", ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingKind.Function, "anon.partial(cpf,0,$$*********$$,2)", ColumnSensitivity.High),
        ], Now);
        Catalog.AnonymizationProfiles.Items.Add(Profile);
        Inspector.DestinationId = Development.Id;
    }

    public DatabaseCopyRequest Request(DatabaseCopyOptions? options = null, DatabaseOperationType operation = DatabaseOperationType.CopyAndAnonymize) =>
        new(Production.Id, Development.Id, operation, Profile.Id, options ?? DatabaseCopyOptions.Default);

    public DatabaseCopyPlanner Planner() => new(Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles);

    public PostgresAnonymizationService Anonymization() => new(Anonymizer, Inspector, Tools, Policy);

    public RunDatabaseCopyHandler RunHandler() => new(
        Planner(),
        Policy,
        Locator,
        Inspector,
        Anonymization(),
        Tools,
        Tools,
        Workspaces,
        Catalog.Audit,
        Catalog,
        Gate,
        new FakeCurrentUser(),
        Clock,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDatabaseCopyHandler>.Instance);

    public ValidateDatabaseCopyHandler ValidateHandler() => new(Planner(), Policy, Locator, Inspector, Anonymization());
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
