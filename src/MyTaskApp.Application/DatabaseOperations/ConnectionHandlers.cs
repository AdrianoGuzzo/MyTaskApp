using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>Uma conexão como a lista e o formulário a mostram. Sem senha: só se há uma guardada.</summary>
public sealed record DatabaseConnectionRow(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    DatabaseEnvironment Environment,
    DatabaseSslMode SslMode,
    string? Description,
    bool IsEnabled,
    ConnectionPermissions Permissions,
    bool HasPassword,
    DateTimeOffset UpdatedAt)
{
    public bool IsProtected => EnvironmentPolicy.IsProtected(Environment);
}

public sealed record GetDatabaseConnections;

public sealed class GetDatabaseConnectionsHandler(
    IDatabaseConnectionRepository connections,
    IDatabaseCredentialStore credentials)
{
    public async Task<IReadOnlyList<DatabaseConnectionRow>> HandleAsync(
        GetDatabaseConnections query,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<DatabaseConnectionRow>();

        foreach (var connection in await connections.ListAsync(cancellationToken))
        {
            rows.Add(new DatabaseConnectionRow(
                connection.Id,
                connection.Name,
                connection.Host,
                connection.Port,
                connection.Database,
                connection.Username,
                connection.Environment,
                connection.SslMode,
                connection.Description,
                connection.IsEnabled,
                connection.Permissions,
                await credentials.HasAsync(connection.SecretReference, cancellationToken),
                connection.UpdatedAt));
        }

        return rows;
    }
}

/// <summary>
/// Criar (<see cref="Id"/> nulo) ou editar uma conexão. <see cref="Password"/>
/// nulo mantém a senha guardada; preenchido, troca. É <see cref="SecretText"/>
/// para este comando poder ir a um log sem levar a senha junto.
/// </summary>
public sealed record SaveDatabaseConnection(
    Guid? Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    DatabaseEnvironment Environment,
    DatabaseSslMode SslMode,
    string? Description,
    ConnectionPermissions Requested,
    SecretText? Password);

public sealed class SaveDatabaseConnectionHandler(
    IDatabaseConnectionRepository connections,
    IDatabaseCredentialStore credentials,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SaveDatabaseConnectionHandler> logger)
{
    public async Task<Guid> HandleAsync(SaveDatabaseConnection command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        if (await connections.NameExistsAsync(command.Name ?? string.Empty, command.Id, cancellationToken))
        {
            throw new DomainException($"Já existe uma conexão chamada {command.Name?.Trim()}.");
        }

        DatabaseConnection connection;

        if (command.Id is { } id)
        {
            connection = await connections.GetByIdAsync(id, cancellationToken);
            connection.Update(
                command.Name!, command.Host, command.Port, command.Database, command.Username,
                command.Environment, command.SslMode, command.Description, command.Requested, now);
        }
        else
        {
            connection = DatabaseConnection.Create(
                command.Name!, command.Host, command.Port, command.Database, command.Username,
                command.Environment, command.SslMode, command.Description, command.Requested, now);
            await connections.AddAsync(connection, cancellationToken);
        }

        // O cofre antes do banco: se o sistema recusar guardar (Linux sem
        // secret-tool), nada é gravado e a mensagem diz o porquê.
        if (command.Password is { } password)
        {
            connection.AttachSecret(await credentials.StoreAsync(connection.Id, password, cancellationToken), now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "DatabaseConnectionSaved {ConnectionId} {Environment} {PasswordChanged}",
            connection.Id,
            connection.Environment,
            command.Password is not null);

        return connection.Id;
    }
}

public sealed record SetDatabaseConnectionEnabled(Guid Id, bool Enabled);

public sealed class SetDatabaseConnectionEnabledHandler(
    IDatabaseConnectionRepository connections,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetDatabaseConnectionEnabled command, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(command.Id, cancellationToken);
        connection.SetEnabled(command.Enabled, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed record DeleteDatabaseConnection(Guid Id);

public sealed class DeleteDatabaseConnectionHandler(
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository anonymizationProfiles,
    IDatabaseCopyProfileRepository copyProfiles,
    IDatabaseCredentialStore credentials,
    IUnitOfWork unitOfWork,
    ILogger<DeleteDatabaseConnectionHandler> logger)
{
    public async Task HandleAsync(DeleteDatabaseConnection command, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(command.Id, cancellationToken);

        if (await copyProfiles.AnyUsesConnectionAsync(connection.Id, cancellationToken)
            || await anonymizationProfiles.AnyUsesConnectionAsync(connection.Id, cancellationToken))
        {
            throw new DomainException($"{connection.Name} é usada por um perfil. Exclua ou altere o perfil antes.");
        }

        var reference = connection.SecretReference;
        connections.Remove(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Depois do banco: um cofre que falhe deixa um segredo órfão, não uma conexão sem senha.
        await credentials.DeleteAsync(reference, cancellationToken);
        logger.LogInformation("DatabaseConnectionDeleted {ConnectionId}", command.Id);
    }
}

/// <summary>
/// Testar uma conexão — salva (<see cref="Id"/>) ou ainda no formulário. A
/// senha digitada vale só para o teste; sem ela, vale a guardada.
/// </summary>
public sealed record TestDatabaseConnection(
    Guid? Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    DatabaseEnvironment Environment,
    DatabaseSslMode SslMode,
    SecretText? Password);

public sealed class TestDatabaseConnectionHandler(
    IDatabaseConnectionRepository connections,
    IPostgresServerInspector inspector,
    IDatabaseSecurityPolicy policy,
    TimeProvider timeProvider,
    ILogger<TestDatabaseConnectionHandler> logger)
{
    public async Task<ServerDiagnostics> HandleAsync(TestDatabaseConnection command, CancellationToken cancellationToken = default)
    {
        var saved = command.Id is { } id ? await connections.GetByIdAsync(id, cancellationToken) : null;

        // O rascunho passa pela mesma validação do cadastro: host com hífen não chega à rede.
        var draft = DatabaseConnection.Create(
            string.IsNullOrWhiteSpace(command.Name) ? "Teste" : command.Name,
            command.Host,
            command.Port,
            command.Database,
            command.Username,
            command.Environment,
            command.SslMode,
            description: null,
            default,
            timeProvider.GetUtcNow());

        var snapshot = draft.Snapshot() with
        {
            Id = saved?.Id ?? draft.Id,
            SecretReference = saved?.SecretReference,
        };

        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.TestConnection, snapshot));

        var result = await inspector.TestAsync(snapshot, command.Password, cancellationToken);
        logger.LogInformation("DatabaseConnectionTested {ConnectionId} {Connected}", snapshot.Id, result.Connected);
        return result;
    }
}
