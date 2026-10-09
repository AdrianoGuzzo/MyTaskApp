using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

// --- Perfis de anonimização ---------------------------------------------------

public sealed record AnonymizationRuleRow(
    string Schema,
    string Table,
    string Column,
    MaskingKind Kind,
    string Expression,
    ColumnSensitivity Sensitivity)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";

    public AnonymizationRuleSpec ToSpec() => new(Schema, Table, Column, Kind, Expression, Sensitivity);
}

public sealed record AnonymizationProfileRow(
    Guid Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    string PolicyName,
    bool IsEnabled,
    IReadOnlyList<AnonymizationRuleRow> Rules,
    DateTimeOffset UpdatedAt);

public sealed record GetAnonymizationProfiles;

public sealed class GetAnonymizationProfilesHandler(IAnonymizationProfileRepository profiles)
{
    public async Task<IReadOnlyList<AnonymizationProfileRow>> HandleAsync(
        GetAnonymizationProfiles query,
        CancellationToken cancellationToken = default) =>
        (await profiles.ListAsync(cancellationToken)).Select(ToRow).ToList();

    internal static AnonymizationProfileRow ToRow(AnonymizationProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.Description,
        profile.ConnectionId,
        profile.PolicyName,
        profile.IsEnabled,
        profile.Rules
            .OrderBy(rule => rule.Schema, StringComparer.Ordinal)
            .ThenBy(rule => rule.Table, StringComparer.Ordinal)
            .ThenBy(rule => rule.Column, StringComparer.Ordinal)
            .Select(rule => new AnonymizationRuleRow(rule.Schema, rule.Table, rule.Column, rule.Kind, rule.Expression, rule.Sensitivity))
            .ToList(),
        profile.UpdatedAt);
}

/// <summary>Criar ou editar um perfil, com o conjunto inteiro de regras confirmadas.</summary>
public sealed record SaveAnonymizationProfile(
    Guid? Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    string? PolicyName,
    IReadOnlyList<AnonymizationRuleRow> Rules);

public sealed class SaveAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    IDatabaseConnectionRepository connections,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SaveAnonymizationProfileHandler> logger)
{
    public async Task<Guid> HandleAsync(SaveAnonymizationProfile command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await connections.GetByIdAsync(command.ConnectionId, cancellationToken);

        AnonymizationProfile profile;

        if (command.Id is { } id)
        {
            profile = await profiles.GetByIdAsync(id, cancellationToken);
            profile.Update(command.Name, command.Description, command.ConnectionId, command.PolicyName, now);
        }
        else
        {
            profile = AnonymizationProfile.Create(command.Name, command.Description, command.ConnectionId, command.PolicyName, now);
            await profiles.AddAsync(profile, cancellationToken);
        }

        profile.ReplaceRules(command.Rules.Select(rule => rule.ToSpec()), now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("AnonymizationProfileSaved {ProfileId} {Rules}", profile.Id, profile.Rules.Count);
        return profile.Id;
    }
}

public sealed record SetAnonymizationProfileEnabled(Guid Id, bool Enabled);

public sealed class SetAnonymizationProfileEnabledHandler(
    IAnonymizationProfileRepository profiles,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetAnonymizationProfileEnabled command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);
        profile.SetEnabled(command.Enabled, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed record DeleteAnonymizationProfile(Guid Id);

public sealed class DeleteAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    IDatabaseCopyProfileRepository copyProfiles,
    ISavedDatabaseRepository savedDatabases,
    IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(DeleteAnonymizationProfile command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);

        if (await copyProfiles.AnyUsesAnonymizationProfileAsync(profile.Id, cancellationToken))
        {
            throw new DomainException($"{profile.Name} é usado por um perfil de cópia. Altere ou exclua o perfil de cópia antes.");
        }

        if (await savedDatabases.AnyUsesAnonymizationProfileAsync(profile.Id, cancellationToken))
        {
            throw new DomainException($"{profile.Name} é usado por um apelido. Altere ou exclua o apelido antes.");
        }

        profiles.Remove(profile);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// As colunas que parecem dado pessoal na conexão, para o usuário confirmar ou
/// descartar. <see cref="Database"/>: o banco, quando a conexão é só o servidor.
/// </summary>
public sealed record SuggestSensitiveColumns(Guid ConnectionId, string? Database = null);

public sealed class SuggestSensitiveColumnsHandler(
    IDatabaseConnectionRepository connections,
    IPostgresServerInspector inspector,
    IDatabaseSecurityPolicy policy)
{
    public async Task<IReadOnlyList<ColumnSuggestion>> HandleAsync(
        SuggestSensitiveColumns query,
        CancellationToken cancellationToken = default)
    {
        var connection = DatabaseChoice.Resolve(
            (await connections.GetByIdAsync(query.ConnectionId, cancellationToken)).Snapshot(),
            query.Database);
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.InspectDatabase, connection));

        var columns = await inspector.ListColumnsAsync(connection, cancellationToken);
        return SensitiveColumnClassifier.Suggest(columns);
    }
}

/// <summary>O script <c>SECURITY LABEL</c> do perfil, para o DBA. O app não o executa.</summary>
public sealed record GenerateMaskingScript(Guid ProfileId, string? Database = null);

public sealed class GenerateMaskingScriptHandler(
    IAnonymizationProfileRepository profiles,
    IDatabaseConnectionRepository connections,
    TimeProvider timeProvider)
{
    public async Task<string> HandleAsync(GenerateMaskingScript query, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.ProfileId, cancellationToken);
        var masked = DatabaseChoice.Resolve((await connections.GetByIdAsync(profile.ConnectionId, cancellationToken)).Snapshot(), query.Database);
        return MaskingScriptBuilder.Build(profile, masked.Username, masked.Database!, timeProvider.GetUtcNow());
    }
}

public sealed record ValidateAnonymizationProfile(Guid ProfileId, string? Database = null);

public sealed class ValidateAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    IDatabaseConnectionRepository connections,
    IPostgresAnonymizationService anonymization)
{
    public async Task<AnonymizationValidation> HandleAsync(
        ValidateAnonymizationProfile query,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.ProfileId, cancellationToken);
        var masked = DatabaseChoice.Resolve((await connections.GetByIdAsync(profile.ConnectionId, cancellationToken)).Snapshot(), query.Database);
        return await anonymization.ValidateAsync(profile, masked, cancellationToken);
    }
}

// --- Perfis de cópia ---------------------------------------------------------

public sealed record DatabaseCopyProfileRow(
    Guid Id,
    string Name,
    Guid SourceConnectionId,
    Guid DestinationConnectionId,
    Guid? AnonymizationProfileId,
    DatabaseCopyOptions Options,
    bool IsEnabled,
    DateTimeOffset UpdatedAt,
    string? SourceDatabase = null);

public sealed record GetDatabaseCopyProfiles;

public sealed class GetDatabaseCopyProfilesHandler(IDatabaseCopyProfileRepository profiles)
{
    public async Task<IReadOnlyList<DatabaseCopyProfileRow>> HandleAsync(
        GetDatabaseCopyProfiles query,
        CancellationToken cancellationToken = default) =>
        (await profiles.ListAsync(cancellationToken))
            .Select(profile => new DatabaseCopyProfileRow(
                profile.Id,
                profile.Name,
                profile.SourceConnectionId,
                profile.DestinationConnectionId,
                profile.AnonymizationProfileId,
                profile.Options,
                profile.IsEnabled,
                profile.UpdatedAt,
                profile.SourceDatabase))
            .ToList();
}

public sealed record SaveDatabaseCopyProfile(
    Guid? Id,
    string Name,
    Guid SourceConnectionId,
    Guid DestinationConnectionId,
    Guid? AnonymizationProfileId,
    DatabaseCopyOptions Options,
    string? SourceDatabase = null);

/// <summary>
/// Grava o perfil e já o julga pela política com as conexões de hoje: um
/// perfil "Desenvolvimento → Produção" não chega nem a ser salvo.
/// </summary>
public sealed class SaveDatabaseCopyProfileHandler(
    IDatabaseCopyProfileRepository profiles,
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository anonymizationProfiles,
    IDatabaseSecurityPolicy policy,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Guid> HandleAsync(SaveDatabaseCopyProfile command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var source = (await connections.GetByIdAsync(command.SourceConnectionId, cancellationToken)).Snapshot();
        var destination = (await connections.GetByIdAsync(command.DestinationConnectionId, cancellationToken)).Snapshot();

        if (destination.IsProtected)
        {
            throw new DatabaseSecurityException(policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.Restore,
                Destination: destination)));
        }

        if (command.AnonymizationProfileId is { } anonymizationId)
        {
            await anonymizationProfiles.GetByIdAsync(anonymizationId, cancellationToken);
        }

        if (!command.Options.RequireAnonymization && source.Permissions.RequireAnonymization)
        {
            throw new DomainException($"{source.Name} exige anonimização: a cópia não pode dispensá-la.");
        }

        DatabaseCopyProfile profile;

        if (command.Id is { } id)
        {
            profile = await profiles.GetByIdAsync(id, cancellationToken);
            profile.Update(command.Name, source.Id, destination.Id, command.AnonymizationProfileId, command.Options, now, command.SourceDatabase);
        }
        else
        {
            profile = DatabaseCopyProfile.Create(
                command.Name, source.Id, destination.Id, command.AnonymizationProfileId, command.Options, now, command.SourceDatabase);
            await profiles.AddAsync(profile, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }
}

public sealed record SetDatabaseCopyProfileEnabled(Guid Id, bool Enabled);

public sealed class SetDatabaseCopyProfileEnabledHandler(
    IDatabaseCopyProfileRepository profiles,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetDatabaseCopyProfileEnabled command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);
        profile.SetEnabled(command.Enabled, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed record DeleteDatabaseCopyProfile(Guid Id);

public sealed class DeleteDatabaseCopyProfileHandler(IDatabaseCopyProfileRepository profiles, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(DeleteDatabaseCopyProfile command, CancellationToken cancellationToken = default)
    {
        profiles.Remove(await profiles.GetByIdAsync(command.Id, cancellationToken));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// --- Diagnóstico e histórico ---------------------------------------------------

public sealed record DetectPostgresTools(bool Refresh);

public sealed class DetectPostgresToolsHandler(IPostgresEnvironmentDiagnostics diagnostics)
{
    public Task<EnvironmentDiagnosticsReport> HandleAsync(DetectPostgresTools query, CancellationToken cancellationToken = default) =>
        diagnostics.DiagnoseToolsAsync(query.Refresh, cancellationToken);
}

/// <summary>Diagnóstico completo de uma conexão. Com perfil, o Anonymizer é visto pela política dele.</summary>
public sealed record DiagnoseDatabase(Guid ConnectionId, string? PolicyName, bool Refresh);

public sealed class DiagnoseDatabaseHandler(
    IDatabaseConnectionRepository connections,
    IPostgresEnvironmentDiagnostics diagnostics)
{
    public async Task<EnvironmentDiagnosticsReport> HandleAsync(DiagnoseDatabase query, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(query.ConnectionId, cancellationToken);
        return await diagnostics.DiagnoseAsync(
            connection.Snapshot(),
            string.IsNullOrWhiteSpace(query.PolicyName) ? AnonymizationProfile.DefaultPolicyName : query.PolicyName,
            query.Refresh,
            cancellationToken);
    }
}

public sealed record DatabaseOperationRow(
    Guid Id,
    DatabaseOperationType OperationType,
    DatabaseOperationStatus Status,
    string? Source,
    string? Destination,
    string? Profile,
    string? AnonymizationProfile,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    string Host,
    string User,
    string? ToolVersions,
    string? SourceDatabaseVersion,
    string? DestinationDatabaseVersion,
    long? AnonymousDumpSize,
    int? MaskedColumnsCount,
    string? Summary,
    string? Error,
    string? SourceDatabase = null,
    string? DestinationDatabase = null);

public sealed record GetDatabaseOperationHistory(int Limit = 100);

public sealed class GetDatabaseOperationHistoryHandler(IDatabaseOperationAuditLog audit)
{
    public async Task<IReadOnlyList<DatabaseOperationRow>> HandleAsync(
        GetDatabaseOperationHistory query,
        CancellationToken cancellationToken = default) =>
        (await audit.ListRecentAsync(query.Limit, cancellationToken))
            .Select(entry => new DatabaseOperationRow(
                entry.Id,
                entry.OperationType,
                entry.Status,
                entry.SourceConnectionName,
                entry.DestinationConnectionName,
                entry.ProfileName,
                entry.AnonymizationProfile,
                entry.StartedAt,
                entry.CompletedAt,
                entry.Duration,
                entry.Host,
                entry.User,
                entry.ToolVersions,
                entry.SourceDatabaseVersion,
                entry.DestinationDatabaseVersion,
                entry.AnonymousDumpSize,
                entry.MaskedColumnsCount,
                entry.Summary,
                entry.Error,
                entry.SourceDatabase,
                entry.DestinationDatabase))
            .ToList();
}

/// <summary>
/// Na volta do app: operações "em andamento" que ninguém está rodando viram
/// interrompidas, e dumps esquecidos por uma queda são apagados.
/// </summary>
public sealed record RecoverInterruptedDatabaseOperations;

public sealed class RecoverInterruptedDatabaseOperationsHandler(
    IDatabaseOperationAuditLog audit,
    IDatabaseOperationWorkspaceFactory workspaces,
    DatabaseOperationGate gate,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RecoverInterruptedDatabaseOperationsHandler> logger)
{
    public async Task<int> HandleAsync(RecoverInterruptedDatabaseOperations command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var interrupted = 0;

        foreach (var entry in await audit.ListRunningAsync(cancellationToken))
        {
            if (gate.IsLive(entry.Id))
            {
                continue;
            }

            entry.Interrupt(now);
            interrupted++;
        }

        if (interrupted > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var swept = await workspaces.SweepAsync(gate.LiveOperations, cancellationToken);

        if (interrupted > 0 || swept > 0)
        {
            logger.LogWarning("DatabaseOperationsRecovered {Interrupted} {Swept}", interrupted, swept);
        }

        return interrupted;
    }
}

/// <summary>
/// Uma operação de banco por vez, no app inteiro (ADR-056). Singleton: sabe
/// quais operações estão vivas, para a recuperação não chamar de
/// "interrompida" uma cópia que ainda está rodando.
/// </summary>
public sealed class DatabaseOperationGate
{
    private readonly Lock _gate = new();

    private readonly HashSet<Guid> _live = [];

    public IReadOnlyCollection<Guid> LiveOperations
    {
        get
        {
            lock (_gate)
            {
                return [.. _live];
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _live.Count > 0;
            }
        }
    }

    public bool IsLive(Guid operationId)
    {
        lock (_gate)
        {
            return _live.Contains(operationId);
        }
    }

    /// <summary>Entra, ou recusa com mensagem se outra operação está rodando.</summary>
    public IDisposable Enter(Guid operationId)
    {
        lock (_gate)
        {
            if (_live.Count > 0)
            {
                throw new DomainException("Já há uma operação de banco em andamento. Espere terminar ou cancele.");
            }

            _live.Add(operationId);
        }

        return new Release(this, operationId);
    }

    private sealed class Release(DatabaseOperationGate gate, Guid operationId) : IDisposable
    {
        public void Dispose()
        {
            lock (gate._gate)
            {
                gate._live.Remove(operationId);
            }
        }
    }
}

/// <summary>O banco de uma leitura: o fixo da conexão, ou o informado quando ela é só o servidor (ADR-057).</summary>
internal static class DatabaseChoice
{
    public static DatabaseConnectionSnapshot Resolve(DatabaseConnectionSnapshot connection, string? chosen)
    {
        if (connection.Database is { } fixedDatabase)
        {
            return string.IsNullOrWhiteSpace(chosen) || string.Equals(fixedDatabase, chosen.Trim(), StringComparison.Ordinal)
                ? connection
                : throw new DomainException($"{connection.Name} só acessa o banco {fixedDatabase}.");
        }

        return string.IsNullOrWhiteSpace(chosen)
            ? throw new DomainException($"{connection.Name} é só o servidor: informe o banco.")
            : connection.WithDatabase(chosen);
    }
}
