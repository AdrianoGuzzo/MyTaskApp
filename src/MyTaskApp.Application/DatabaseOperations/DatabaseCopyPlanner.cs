using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>
/// Uma cópia pedida pela tela: origem, destino, tipo e opções. Pode vir de um
/// perfil de cópia (<see cref="CopyProfileId"/>, para a auditoria) ou ser avulsa.
/// <see cref="SourceDatabase"/> é o banco escolhido quando a origem é só o
/// servidor; <see cref="SavedDatabaseId"/>, o apelido escolhido (ADR-057).
/// </summary>
public sealed record DatabaseCopyRequest(
    Guid SourceConnectionId,
    Guid DestinationConnectionId,
    DatabaseOperationType Operation,
    Guid? AnonymizationProfileId,
    DatabaseCopyOptions Options,
    Guid? CopyProfileId = null,
    string? SourceDatabase = null,
    Guid? SavedDatabaseId = null);

/// <summary>
/// Tudo o que a cópia precisa, lido do banco do app — nunca da tela. As
/// conexões já vêm com o banco resolvido: o escolhido na cópia, ou o nome
/// gerado no destino (<see cref="NewDestinationDatabase"/>).
/// </summary>
public sealed record DatabaseCopyPlan(
    DatabaseCopyRequest Request,
    DatabaseConnectionSnapshot Source,
    DatabaseConnectionSnapshot Destination,
    AnonymizationProfile? AnonymizationProfile,
    string? CopyProfileName,
    IReadOnlyCollection<string> ProtectedEndpoints,
    bool NewDestinationDatabase = false)
{
    public bool Anonymizes => Request.Operation == DatabaseOperationType.CopyAndAnonymize;

    public EnvironmentRules SourceRules => EnvironmentPolicy.For(Source.Environment);

    /// <summary>Apagar o destino antes: só num banco fixo, com "Recriar o destino". Um banco novo nunca é apagado.</summary>
    public bool DropsDestination => !NewDestinationDatabase && Request.Options.RecreateDestination;

    /// <summary>Criar o destino: ao recriá-lo, ou sempre que o nome é gerado.</summary>
    public bool CreatesDestination => NewDestinationDatabase || Request.Options.RecreateDestination;

    /// <summary>O que se sabe da anonimização só pelo cadastro.</summary>
    public AnonymizationFacts RegisteredFacts => AnonymizationProfile is { } profile
        ? new AnonymizationFacts(true, profile.IsEnabled, profile.Rules.Count)
        : AnonymizationFacts.None;

    public DatabaseOperationRequest ToPolicyRequest(AnonymizationFacts? facts = null) => new(
        Request.Operation,
        Source,
        Destination,
        facts ?? RegisteredFacts,
        Request.Options,
        ProtectedEndpoints,
        NewDestinationDatabase);
}

/// <summary>
/// Monta o <see cref="DatabaseCopyPlan"/>: conexões, perfil, e as chaves de todo
/// banco de produção cadastrado. É aqui que o banco de cada lado é resolvido
/// (ADR-057) — daqui para a frente, política, guard e ferramentas veem sempre
/// um banco concreto.
/// </summary>
public sealed class DatabaseCopyPlanner(
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository anonymizationProfiles,
    IDatabaseCopyProfileRepository copyProfiles,
    ISavedDatabaseRepository savedDatabases,
    TimeProvider timeProvider)
{
    public async Task<DatabaseCopyPlan> PlanAsync(DatabaseCopyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation is not (DatabaseOperationType.Copy or DatabaseOperationType.CopyAndAnonymize))
        {
            throw new DomainException("Escolha Copiar ou Copiar + Anonimizar.");
        }

        var all = await connections.ListAsync(cancellationToken);
        var source = ResolveSource(Find(all, request.SourceConnectionId, "origem"), request.SourceDatabase);
        var destination = Find(all, request.DestinationConnectionId, "destino");

        string? alias = null;

        if (request.SavedDatabaseId is { } savedId)
        {
            var saved = await savedDatabases.GetByIdAsync(savedId, cancellationToken);

            // O apelido só nomeia o destino; o que vale é o pedido. Se não
            // batem, a tela mudou um campo depois de escolher o apelido.
            if (saved.ConnectionId != source.Id
                || !string.Equals(saved.DatabaseName, source.Database, StringComparison.Ordinal))
            {
                throw new DomainException($"A origem não é mais a do apelido {saved.Alias}. Escolha o apelido de novo.");
            }

            alias = saved.Alias;
        }

        AnonymizationProfile? profile = null;

        // As regras valem para o banco escolhido na origem: o SELECT da cópia é montado nele (ADR-058).
        if (request.AnonymizationProfileId is { } profileId && request.Operation == DatabaseOperationType.CopyAndAnonymize)
        {
            profile = await anonymizationProfiles.FindByIdAsync(profileId, cancellationToken)
                ?? throw new DomainException("Perfil de anonimização não encontrado.");
        }

        var newDestination = !destination.HasDatabase;

        if (newDestination && source.Database is { } copied)
        {
            var at = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
            destination = destination.WithDatabase(SavedDatabase.CopyName(alias ?? copied, at));
        }

        string? copyProfileName = null;

        if (request.CopyProfileId is { } copyProfileId)
        {
            copyProfileName = (await copyProfiles.FindByIdAsync(copyProfileId, cancellationToken))?.Name;
        }

        // Uma produção sem banco protege o servidor inteiro: a chave dela é host:porta/*.
        var protectedEndpoints = all
            .Where(connection => connection.IsProtected)
            .Select(connection => connection.Snapshot().EndpointKey)
            .ToHashSet(StringComparer.Ordinal);

        return new DatabaseCopyPlan(request, source, destination, profile, copyProfileName, protectedEndpoints, newDestination);
    }

    private static DatabaseConnectionSnapshot ResolveSource(DatabaseConnectionSnapshot source, string? chosen)
    {
        if (string.IsNullOrWhiteSpace(chosen))
        {
            // Sem banco escolhido numa conexão só de servidor: segue sem banco,
            // e a política diz "escolha o banco de origem" junto com o resto.
            return source;
        }

        if (source.Database is { } fixedDatabase)
        {
            return string.Equals(fixedDatabase, chosen.Trim(), StringComparison.Ordinal)
                ? source
                : throw new DomainException($"{source.Name} só acessa o banco {fixedDatabase}.");
        }

        return source.WithDatabase(chosen);
    }

    private static DatabaseConnectionSnapshot Find(IReadOnlyList<DatabaseConnection> all, Guid id, string side) =>
        all.FirstOrDefault(connection => connection.Id == id)?.Snapshot()
        ?? throw new DomainException($"Conexão de {side} não encontrada.");
}
