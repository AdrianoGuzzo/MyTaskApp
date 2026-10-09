namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// O registro de uma operação de banco (ADR-056): quem, de onde, para onde,
/// com que ferramentas, quanto e como terminou.
/// </summary>
/// <remarks>
/// <para>
/// O ciclo de vida é o da <c>CommandExecution</c>: gravado como
/// <see cref="DatabaseOperationStatus.Running"/> <b>antes</b> do primeiro
/// processo, para uma queda deixar rastro. Os campos são os do
/// <c>TaskAuditEntry</c>: nomes copiados, sem chave estrangeira — a auditoria
/// sobrevive à conexão excluída.
/// </para>
/// <para>
/// <b>Nunca guarda segredo nem dado pessoal.</b> Todo texto passa por
/// <see cref="SensitiveText.Mask"/> aqui dentro, e não por cuidado de quem
/// chama; é cortado em vez de recusado, porque uma auditoria que lança
/// derrubaria a operação que deveria só registrar.
/// </para>
/// <para>
/// <see cref="Host"/> e <see cref="User"/> são a máquina e o usuário do sistema
/// que executaram a operação; os servidores de banco estão nos nomes das conexões.
/// </para>
/// </remarks>
public sealed class DatabaseOperationAudit
{
    public const int MaxNameLength = 200;

    public const int MaxErrorLength = 2000;

    public const int MaxToolVersionsLength = 500;

    public const int MaxVersionLength = 200;

    public const int MaxSummaryLength = 2000;

    // Exigido pela materialização do EF Core.
    private DatabaseOperationAudit()
    {
        Host = string.Empty;
        User = string.Empty;
    }

    public Guid Id { get; private set; }

    public DatabaseOperationType OperationType { get; private set; }

    public Guid? SourceConnectionId { get; private set; }

    public string? SourceConnectionName { get; private set; }

    public Guid? DestinationConnectionId { get; private set; }

    public string? DestinationConnectionName { get; private set; }

    /// <summary>O perfil de cópia, quando a operação veio de um.</summary>
    public Guid? ProfileId { get; private set; }

    public string? ProfileName { get; private set; }

    public string? AnonymizationProfile { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DatabaseOperationStatus Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>A máquina que executou.</summary>
    public string Host { get; private set; }

    /// <summary>O usuário do sistema que executou.</summary>
    public string User { get; private set; }

    /// <summary>"pg_dump 17.2; pg_restore 17.2" — o que de fato rodou.</summary>
    public string? ToolVersions { get; private set; }

    public string? SourceDatabaseVersion { get; private set; }

    public string? DestinationDatabaseVersion { get; private set; }

    public long? RowsProcessed { get; private set; }

    /// <summary>Tamanho do dump sem anonimização. Num fluxo de produção fica vazio: esse dump não existe.</summary>
    public long? DumpSize { get; private set; }

    public long? AnonymousDumpSize { get; private set; }

    public TimeSpan? Duration { get; private set; }

    public int? MaskedColumnsCount { get; private set; }

    /// <summary>O resultado da verificação, em uma linha por item ("Schema: PASS").</summary>
    public string? Summary { get; private set; }

    public bool IsRunning => Status == DatabaseOperationStatus.Running;

    public static DatabaseOperationAudit Start(
        DatabaseOperationType operationType,
        DatabaseConnectionSnapshot? source,
        DatabaseConnectionSnapshot? destination,
        Guid? profileId,
        string? profileName,
        string host,
        string user,
        DateTimeOffset startedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(startedAt),
            OperationType = operationType,
            SourceConnectionId = source?.Id,
            SourceConnectionName = Clean(source?.Name, MaxNameLength),
            DestinationConnectionId = destination?.Id,
            DestinationConnectionName = Clean(destination?.Name, MaxNameLength),
            ProfileId = profileId,
            ProfileName = Clean(profileName, MaxNameLength),
            StartedAt = startedAt,
            Status = DatabaseOperationStatus.Running,
            Host = Clean(host, MaxNameLength) ?? "?",
            User = Clean(user, MaxNameLength) ?? "?",
        };

    /// <summary>Recusada pela política: nasce já encerrada, com os motivos.</summary>
    public static DatabaseOperationAudit Blocked(
        DatabaseOperationType operationType,
        DatabaseConnectionSnapshot? source,
        DatabaseConnectionSnapshot? destination,
        Guid? profileId,
        string? profileName,
        string host,
        string user,
        string reasons,
        DateTimeOffset at)
    {
        var audit = Start(operationType, source, destination, profileId, profileName, host, user, at);
        audit.Complete(DatabaseOperationStatus.Blocked, at, reasons);
        return audit;
    }

    public void RecordTools(string? toolVersions) => ToolVersions = Clean(toolVersions, MaxToolVersionsLength);

    public void RecordServerVersions(string? source, string? destination)
    {
        SourceDatabaseVersion = Clean(source, MaxVersionLength) ?? SourceDatabaseVersion;
        DestinationDatabaseVersion = Clean(destination, MaxVersionLength) ?? DestinationDatabaseVersion;
    }

    public void RecordAnonymization(string? profileName, int? maskedColumns)
    {
        AnonymizationProfile = Clean(profileName, MaxNameLength);
        MaskedColumnsCount = maskedColumns is < 0 ? null : maskedColumns;
    }

    public void RecordSizes(long? dumpSize, long? anonymousDumpSize)
    {
        DumpSize = dumpSize is < 0 ? null : dumpSize ?? DumpSize;
        AnonymousDumpSize = anonymousDumpSize is < 0 ? null : anonymousDumpSize ?? AnonymousDumpSize;
    }

    public void RecordRows(long? rows) => RowsProcessed = rows is < 0 ? null : rows;

    public void RecordSummary(string? summary) => Summary = Clean(summary, MaxSummaryLength);

    public void Succeed(DateTimeOffset at) => Complete(DatabaseOperationStatus.Succeeded, at, error: null);

    public void Fail(string? error, DateTimeOffset at) =>
        Complete(DatabaseOperationStatus.Failed, at, error ?? "Falhou sem mensagem.");

    public void Cancel(DateTimeOffset at) => Complete(DatabaseOperationStatus.Canceled, at, "Cancelada pelo usuário.");

    /// <summary>O app caiu no meio: na volta, a operação "em andamento" vira interrompida.</summary>
    public void Interrupt(DateTimeOffset at) =>
        Complete(DatabaseOperationStatus.Interrupted, at, "Interrompida: o app fechou antes de terminar.");

    private void Complete(DatabaseOperationStatus status, DateTimeOffset at, string? error)
    {
        if (!IsRunning)
        {
            throw new DomainException("Esta operação já terminou.");
        }

        Status = status;
        CompletedAt = at < StartedAt ? StartedAt : at;
        Duration = CompletedAt - StartedAt;
        Error = Clean(error, MaxErrorLength);
    }

    /// <summary>Mascara e corta. Nunca lança.</summary>
    private static string? Clean(string? value, int maxLength)
    {
        var normalized = SensitiveText.Mask(value?.Trim());

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
