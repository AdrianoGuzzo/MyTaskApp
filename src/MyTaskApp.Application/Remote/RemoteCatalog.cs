using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O que esta máquina deixa pedir de longe (ADR-088), lido do banco: os
/// diretórios mapeados aqui e os comandos liberados (<c>RemoteAllowed</c>).
/// Negado por padrão: o que não está aqui não existe para o controlador. O
/// <see cref="AgentCatalog.Hash"/> vem vazio; quem sela é <see cref="RemoteCatalogs.Seal"/>.
/// </summary>
public interface IRemoteCatalogQuery
{
    Task<AgentCatalog> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Os tipos de operação que esta versão do agente roda: o que entra no registro e no item 5 de 4.4.</summary>
public sealed record SupportedRemoteOperations(IReadOnlyList<string> Kinds);

public static class RemoteCatalogs
{
    /// <summary>
    /// O hash é do conteúdo, numa ordem fixa: o mesmo catálogo dá o mesmo hash
    /// no desktop e no agente, que leem o mesmo banco e registram os dois.
    /// </summary>
    public static AgentCatalog Seal(AgentCatalog catalog)
    {
        var ordered = catalog with
        {
            Hash = string.Empty,
            Directories = [.. catalog.Directories.OrderBy(directory => directory.TagDirectoryId)],
            Commands = [.. catalog.Commands.OrderBy(command => command.CommandId)],
            Databases = [.. catalog.Databases.OrderBy(database => database.ConnectionId)],
            AgentProfiles = [.. catalog.AgentProfiles.OrderBy(profile => profile.Id, StringComparer.Ordinal)],
        };

        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ordered, RemoteJson.Options)));
        return ordered with { Hash = hash };
    }
}

/// <summary>
/// Publica o catálogo no servidor quando ele muda (<c>PUT …/devices/current/catalog</c>),
/// para o controlador saber o que pedir sem perguntar à máquina. O servidor
/// só aceita de quem é executor: quem chama já conferiu "Aceitar controle".
/// </summary>
public sealed class RemoteCatalogPublisher(
    IUseCaseRunner runner,
    IRemoteOperationsApi remote,
    ILogger<RemoteCatalogPublisher> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _published;

    /// <summary>O catálogo de agora, selado, sem publicar.</summary>
    public async Task<AgentCatalog> CurrentAsync(CancellationToken cancellationToken) =>
        RemoteCatalogs.Seal(await runner.RunAsync<IRemoteCatalogQuery, AgentCatalog>((query, token) => query.ReadAsync(token), cancellationToken));

    /// <summary>Publica se mudou desde a última vez; devolve o catálogo de agora.</summary>
    public async Task<AgentCatalog> PublishAsync(CancellationToken cancellationToken)
    {
        var catalog = await CurrentAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (catalog.Hash != _published)
            {
                await remote.PublishCatalogAsync(catalog, cancellationToken);
                _published = catalog.Hash;
                logger.LogInformation("RemoteCatalogPublished {Hash} {Directories} {Commands}", catalog.Hash[..12], catalog.Directories.Count, catalog.Commands.Count);
            }

            return catalog;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// <c>environment.inspect</c> (E2): Git, Claude, gh e as ferramentas do
/// PostgreSQL, medidos agora, e o catálogo publicado. Não muda nada na máquina.
/// </summary>
public sealed class EnvironmentInspectExecutor(
    MachineCapabilities capabilities,
    RemoteCatalogPublisher catalogs) : IRemoteOperationExecutor
{
    private static readonly string AgentVersion =
        typeof(EnvironmentInspectExecutor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public string Kind => RemoteOperationKinds.EnvironmentInspect;

    public Task<string?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        await execution.EmitAsync(RemoteEventKind.StepStarted, step: "ferramentas", cancellationToken: cancellationToken);
        var tools = await capabilities.GetAsync(cancellationToken, refresh: true);

        await execution.EmitAsync(RemoteEventKind.StepStarted, step: "catálogo", cancellationToken: cancellationToken);
        var catalog = await catalogs.PublishAsync(cancellationToken);

        var inspection = new EnvironmentInspection(
            tools with { CatalogHash = catalog.Hash },
            catalog.Hash,
            catalog.Directories.Count,
            catalog.Commands.Count,
            AgentVersion);

        return RemoteOperationResult.Success(Summary(inspection), JsonSerializer.SerializeToElement(inspection, RemoteJson.Options));
    }

    private static string Summary(EnvironmentInspection inspection)
    {
        var found = new[]
            {
                ("Git", inspection.Capabilities.Git),
                ("Claude", inspection.Capabilities.ClaudeCode),
                ("gh", inspection.Capabilities.GitHubCli),
                ("PostgreSQL", inspection.Capabilities.PostgresTools),
            }
            .Where(tool => tool.Item2.Available)
            .Select(tool => tool.Item1)
            .ToList();

        var tools = found.Count == 0 ? "nenhuma ferramenta" : string.Join(", ", found);
        var commands = inspection.Commands == 1 ? "1 comando liberado" : $"{inspection.Commands} comandos liberados";

        return $"{tools}; {commands}.";
    }
}
