using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

// --- Apelidos de banco (ADR-057) ----------------------------------------------

public sealed record SavedDatabaseRow(
    Guid Id,
    string Alias,
    Guid ConnectionId,
    string DatabaseName,
    Guid? AnonymizationProfileId,
    DateTimeOffset UpdatedAt);

public sealed record GetSavedDatabases;

public sealed class GetSavedDatabasesHandler(ISavedDatabaseRepository savedDatabases)
{
    public async Task<IReadOnlyList<SavedDatabaseRow>> HandleAsync(
        GetSavedDatabases query,
        CancellationToken cancellationToken = default) =>
        (await savedDatabases.ListAsync(cancellationToken))
            .Select(saved => new SavedDatabaseRow(
                saved.Id,
                saved.Alias,
                saved.ConnectionId,
                saved.DatabaseName,
                saved.AnonymizationProfileId,
                saved.UpdatedAt))
            .ToList();
}

/// <summary>Criar (<see cref="Id"/> nulo) ou editar um apelido: origem, banco e anonimização.</summary>
public sealed record SaveSavedDatabase(
    Guid? Id,
    string Alias,
    Guid ConnectionId,
    string DatabaseName,
    Guid? AnonymizationProfileId);

/// <summary>
/// Grava o apelido conferindo o cadastro de hoje: a conexão pode ser origem, o
/// banco cabe nela, e a anonimização lê o mesmo servidor. O resto — produção,
/// destino, Anonymizer no servidor — é da política, a cada cópia.
/// </summary>
public sealed class SaveSavedDatabaseHandler(
    ISavedDatabaseRepository savedDatabases,
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository anonymizationProfiles,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SaveSavedDatabaseHandler> logger)
{
    public async Task<Guid> HandleAsync(SaveSavedDatabase command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var alias = SavedDatabase.NormalizeAlias(command.Alias);

        if (await savedDatabases.AliasExistsAsync(alias, command.Id, cancellationToken))
        {
            throw new DomainException($"Já existe um apelido {alias}.");
        }

        var connection = (await connections.GetByIdAsync(command.ConnectionId, cancellationToken)).Snapshot();

        if (!connection.Permissions.AllowAsSource)
        {
            throw new DomainException($"{connection.Name} não pode ser usada como origem.");
        }

        var database = DatabaseConnection.DatabaseName(command.DatabaseName);

        if (connection.Database is { } fixedDatabase && !string.Equals(fixedDatabase, database, StringComparison.Ordinal))
        {
            throw new DomainException($"{connection.Name} só acessa o banco {fixedDatabase}.");
        }

        if (command.AnonymizationProfileId is { } profileId)
        {
            var profile = await anonymizationProfiles.GetByIdAsync(profileId, cancellationToken);
            var masked = await connections.FindByIdAsync(profile.ConnectionId, cancellationToken);

            if (masked is null || masked.Snapshot().ServerKey != connection.ServerKey)
            {
                throw new DomainException($"A anonimização {profile.Name} não lê o mesmo servidor de {connection.Name}.");
            }

            if (masked.Database is { } maskedDatabase && !string.Equals(maskedDatabase, database, StringComparison.Ordinal))
            {
                throw new DomainException($"A anonimização {profile.Name} é do banco {maskedDatabase}, não de {database}.");
            }
        }

        SavedDatabase saved;

        if (command.Id is { } id)
        {
            saved = await savedDatabases.GetByIdAsync(id, cancellationToken);
            saved.Update(alias, connection.Id, database, command.AnonymizationProfileId, now);
        }
        else
        {
            saved = SavedDatabase.Create(alias, connection.Id, database, command.AnonymizationProfileId, now);
            await savedDatabases.AddAsync(saved, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("SavedDatabaseSaved {SavedDatabaseId} {ConnectionId}", saved.Id, saved.ConnectionId);
        return saved.Id;
    }
}

public sealed record DeleteSavedDatabase(Guid Id);

/// <summary>Apaga só o apelido. Os bancos já copiados com ele continuam no destino.</summary>
public sealed class DeleteSavedDatabaseHandler(ISavedDatabaseRepository savedDatabases, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(DeleteSavedDatabase command, CancellationToken cancellationToken = default)
    {
        savedDatabases.Remove(await savedDatabases.GetByIdAsync(command.Id, cancellationToken));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
