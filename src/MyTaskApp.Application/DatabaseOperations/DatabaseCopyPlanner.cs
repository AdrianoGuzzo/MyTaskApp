using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>
/// Uma cópia pedida pela tela: origem, destino, tipo e opções. Pode vir de um
/// perfil de cópia (<see cref="CopyProfileId"/>, para a auditoria) ou ser avulsa.
/// </summary>
public sealed record DatabaseCopyRequest(
    Guid SourceConnectionId,
    Guid DestinationConnectionId,
    DatabaseOperationType Operation,
    Guid? AnonymizationProfileId,
    DatabaseCopyOptions Options,
    Guid? CopyProfileId = null);

/// <summary>Tudo o que a cópia precisa, lido do banco do app — nunca da tela.</summary>
public sealed record DatabaseCopyPlan(
    DatabaseCopyRequest Request,
    DatabaseConnectionSnapshot Source,
    DatabaseConnectionSnapshot Destination,
    DatabaseConnectionSnapshot? MaskedConnection,
    AnonymizationProfile? AnonymizationProfile,
    string? CopyProfileName,
    IReadOnlyCollection<string> ProtectedEndpoints)
{
    public bool Anonymizes => Request.Operation == DatabaseOperationType.CopyAndAnonymize;

    public EnvironmentRules SourceRules => EnvironmentPolicy.For(Source.Environment);

    /// <summary>O que se sabe da anonimização só pelo cadastro.</summary>
    public AnonymizationFacts RegisteredFacts => AnonymizationProfile is { } profile
        ? new AnonymizationFacts(true, profile.IsEnabled, profile.Rules.Count)
        : AnonymizationFacts.None;

    public DatabaseOperationRequest ToPolicyRequest(AnonymizationFacts? facts = null) => new(
        Request.Operation,
        Source,
        Destination,
        MaskedConnection,
        facts ?? RegisteredFacts,
        Request.Options,
        ProtectedEndpoints);
}

/// <summary>Monta o <see cref="DatabaseCopyPlan"/>: conexões, perfil, e as chaves de todo banco de produção cadastrado.</summary>
public sealed class DatabaseCopyPlanner(
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository anonymizationProfiles,
    IDatabaseCopyProfileRepository copyProfiles)
{
    public async Task<DatabaseCopyPlan> PlanAsync(DatabaseCopyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation is not (DatabaseOperationType.Copy or DatabaseOperationType.CopyAndAnonymize))
        {
            throw new DomainException("Escolha Copiar ou Copiar + Anonimizar.");
        }

        var all = await connections.ListAsync(cancellationToken);
        var source = Find(all, request.SourceConnectionId, "origem");
        var destination = Find(all, request.DestinationConnectionId, "destino");

        AnonymizationProfile? profile = null;
        DatabaseConnectionSnapshot? masked = null;

        if (request.AnonymizationProfileId is { } profileId && request.Operation == DatabaseOperationType.CopyAndAnonymize)
        {
            profile = await anonymizationProfiles.FindByIdAsync(profileId, cancellationToken)
                ?? throw new DomainException("Perfil de anonimização não encontrado.");
            masked = all.FirstOrDefault(connection => connection.Id == profile.ConnectionId)?.Snapshot();
        }

        string? copyProfileName = null;

        if (request.CopyProfileId is { } copyProfileId)
        {
            copyProfileName = (await copyProfiles.FindByIdAsync(copyProfileId, cancellationToken))?.Name;
        }

        var protectedEndpoints = all
            .Where(connection => connection.IsProtected)
            .Select(connection => connection.Snapshot().EndpointKey)
            .ToHashSet(StringComparer.Ordinal);

        return new DatabaseCopyPlan(request, source, destination, masked, profile, copyProfileName, protectedEndpoints);
    }

    private static DatabaseConnectionSnapshot Find(IReadOnlyList<DatabaseConnection> all, Guid id, string side) =>
        all.FirstOrDefault(connection => connection.Id == id)?.Snapshot()
        ?? throw new DomainException($"Conexão de {side} não encontrada.");
}
