using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// O catálogo de cada máquina executora (fase 2; ADR-088): o que o
/// controlador pode escolher lá, por id. O agente publica, a conta lê. O
/// servidor só confere a forma: o que foi liberado é decisão da máquina.
/// </summary>
public sealed class RemoteCatalogUseCases(
    IDeviceRegistry devices,
    IRemoteDeviceStore remote,
    IRemoteCatalogStore catalogs,
    TimeProvider timeProvider,
    ILogger<RemoteCatalogUseCases> logger)
{
    internal const int MaxItems = 500;

    internal const int MaxParameters = 32;

    internal const int MaxTextLength = 200;

    /// <summary>O agente da máquina do token publica o catálogo dela; o hash vai junto para as capacidades.</summary>
    public async Task<AgentCatalog> PublishAsync(SyncCaller caller, AgentCatalog catalog, CancellationToken cancellationToken)
    {
        Validate(catalog);

        var state = (await remote.ListAsync(caller.UserId, cancellationToken)).SingleOrDefault(device => device.DeviceId == caller.DeviceId)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de publicar o catálogo.");

        if (!state.Roles.HasFlag(DeviceRoles.Executor))
        {
            throw new SyncServerException(RemoteErrorCodes.NotAnExecutor, 409, "Só uma máquina que aceita controle remoto publica catálogo.");
        }

        await catalogs.SaveAsync(caller, catalog, timeProvider.GetUtcNow(), cancellationToken);

        logger.LogInformation(
            "RemoteCatalogPublished {UserId} {DeviceId} {Directories} {Commands} {Databases} {Profiles}",
            caller.UserId, caller.DeviceId, catalog.Directories.Count, catalog.Commands.Count, catalog.Databases.Count, catalog.AgentProfiles.Count);

        return catalog;
    }

    /// <summary>O catálogo de uma máquina da conta, não revogada; 404 se ela nunca publicou.</summary>
    public async Task<AgentCatalog> GetAsync(SyncCaller caller, Guid deviceId, CancellationToken cancellationToken)
    {
        if (await devices.FindAsync(caller.UserId, deviceId, cancellationToken) is not { IsRevoked: false })
        {
            throw SyncServerException.NotFound();
        }

        return await catalogs.FindAsync(caller.UserId, deviceId, cancellationToken) ?? throw SyncServerException.NotFound();
    }

    /// <summary>Listas com teto, ids sem repetir e textos curtos, sem caracteres de controle.</summary>
    internal static void Validate(AgentCatalog catalog)
    {
        if (!IsText(catalog.Hash, RemoteDeviceUseCases.MaxCatalogHashLength))
        {
            throw SyncServerException.BadRequest($"O hash do catálogo vai de 1 a {RemoteDeviceUseCases.MaxCatalogHashLength} caracteres.");
        }

        if (new[] { catalog.Directories.Count, catalog.Commands.Count, catalog.Databases.Count, catalog.AgentProfiles.Count }.Any(count => count > MaxItems)
            || catalog.Commands.Any(command => command.Parameters.Count > MaxParameters))
        {
            throw SyncServerException.BadRequest($"Até {MaxItems} itens por lista, e {MaxParameters} parâmetros por comando.");
        }

        if (!Distinct(catalog.Directories.Select(directory => directory.TagDirectoryId))
            || !Distinct(catalog.Commands.Select(command => command.CommandId))
            || !Distinct(catalog.Databases.Select(database => database.ConnectionId))
            || !Distinct(catalog.AgentProfiles.Select(profile => profile.Id)))
        {
            throw SyncServerException.BadRequest("Um id repetido no catálogo.");
        }

        IEnumerable<string?> texts =
        [
            .. catalog.Directories.Select(directory => directory.Label),
            .. catalog.Commands.Select(command => command.Name),
            .. catalog.Commands.SelectMany(command => command.Parameters).SelectMany(parameter => new[] { parameter.Name, parameter.DefaultValue }),
            .. catalog.Databases.SelectMany(database => new[] { database.Name, database.Environment }),
            .. catalog.AgentProfiles.SelectMany(profile => new[] { profile.Id, profile.Name, profile.Description }),
        ];

        if (texts.Any(text => text is not null && !IsText(text, MaxTextLength, allowEmpty: true)))
        {
            throw SyncServerException.BadRequest($"Os nomes do catálogo vão até {MaxTextLength} caracteres, sem caracteres de controle.");
        }

        if (catalog.Commands.Any(command => !Enum.IsDefined(command.Scope)))
        {
            throw SyncServerException.BadRequest("Escopo de comando desconhecido.");
        }
    }

    private static bool Distinct<T>(IEnumerable<T> ids)
    {
        var list = ids.ToList();
        return list.Distinct().Count() == list.Count;
    }

    private static bool IsText(string value, int max, bool allowEmpty = false) =>
        (allowEmpty || value.Length > 0) && value.Length <= max && !ReceivedText.HasControlOrFormat(value);
}

public interface IRemoteCatalogStore
{
    /// <summary>Troca o catálogo da máquina e põe o hash dele nas capacidades registradas.</summary>
    Task SaveAsync(SyncCaller caller, AgentCatalog catalog, DateTimeOffset at, CancellationToken cancellationToken);

    Task<AgentCatalog?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}
