using System.Text.Json;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O que esta máquina, como controladora, pode pedir a outra (T-20, tela "Operação").</summary>
public sealed record GetRemoteControl(Guid DeviceId);

/// <summary>Um ambiente pronto na outra máquina, com o título e a branch lidos aqui.</summary>
/// <param name="TaskTitle">Nulo: a tarefa ainda não chegou por aqui pelo sync.</param>
/// <param name="Commands">Os comandos do catálogo que rodam nele, na ordem do nome.</param>
public sealed record RemoteEnvironmentView(
    Guid TaskId,
    Guid DevelopmentId,
    string? TaskTitle,
    string? Branch,
    IReadOnlyList<CatalogCommand> Commands);

public sealed record RemoteControlView(AgentCatalog Catalog, IReadOnlyList<RemoteEnvironmentView> Environments);

/// <summary>
/// O catálogo que a outra máquina publicou, com os ambientes nomeados pelo que
/// o sync já trouxe para cá: o catálogo só tem ids (ADR-088).
/// </summary>
public sealed class GetRemoteControlHandler(IRemoteOperationsApi remote, ITaskItemRepository tasks)
{
    public async Task<RemoteControlView> HandleAsync(GetRemoteControl query, CancellationToken cancellationToken = default)
    {
        var catalog = await remote.GetCatalogAsync(query.DeviceId, cancellationToken);
        var commands = catalog.Commands.ToDictionary(command => command.CommandId);
        var environments = new List<RemoteEnvironmentView>();

        foreach (var environment in catalog.Environments)
        {
            var task = await tasks.FindByIdAsync(environment.TaskId, cancellationToken);

            environments.Add(new RemoteEnvironmentView(
                environment.TaskId,
                environment.DevelopmentId,
                task?.Title,
                task?.Developments.FirstOrDefault(development => development.Id == environment.DevelopmentId)?.Branch,
                [
                    .. environment.CommandIds
                        .Select(id => commands.GetValueOrDefault(id))
                        .OfType<CatalogCommand>()
                        .OrderBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase),
                ]));
        }

        return new RemoteControlView(
            catalog,
            [.. environments.OrderBy(environment => environment.TaskTitle is null).ThenBy(environment => environment.TaskTitle, StringComparer.CurrentCultureIgnoreCase)]);
    }
}

/// <summary>Os pedidos que a tela monta: só ids e valores, nunca texto de comando (ADR-088).</summary>
public static class RemoteRequests
{
    public static SendRemoteOperation Inspect(Guid deviceId) =>
        new(deviceId, RemoteOperationKinds.EnvironmentInspect, Payload(new EnvironmentInspectionRequest()));

    /// <param name="values">Só os preenchidos: o vazio fica com o padrão de lá.</param>
    public static SendRemoteOperation RunCommand(
        Guid deviceId,
        RemoteEnvironmentView environment,
        CatalogCommand command,
        IReadOnlyDictionary<string, string> values) =>
        new(
            deviceId,
            RemoteOperationKinds.CommandRun,
            Payload(new WorktreeExecutionRequest(
                environment.DevelopmentId,
                command.CommandId,
                values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value.Trim()))),
            environment.TaskId,
            [environment.TaskId]);

    private static JsonElement Payload<T>(T payload) => JsonSerializer.SerializeToElement(payload, RemoteJson.Options);
}

/// <summary>Como vai uma operação pedida daqui, a partir do evento <paramref name="After"/>.</summary>
public sealed record GetRemoteOperationProgress(Guid OperationId, long After);

/// <param name="After">O último evento lido: a próxima leitura parte dele.</param>
public sealed record RemoteOperationProgress(RemoteOperationView Operation, IReadOnlyList<RemoteOperationEvent> Events, long After)
{
    public bool IsFinished => RemoteOperationLifecycle.IsFinal(Operation.Status);
}

/// <summary>
/// O estado e os eventos novos de uma operação. Lê o estado depois dos
/// eventos: o "terminou" nunca chega antes da última linha de saída.
/// </summary>
public sealed class GetRemoteOperationProgressHandler(IRemoteOperationsApi remote)
{
    /// <summary>Páginas por leitura: uma saída que despeja muito chega em mais de uma volta, sem travar a tela.</summary>
    internal const int MaxPages = 4;

    public async Task<RemoteOperationProgress> HandleAsync(GetRemoteOperationProgress query, CancellationToken cancellationToken = default)
    {
        var events = new List<RemoteOperationEvent>();
        var after = query.After;

        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await remote.ListEventsAsync(query.OperationId, after, null, cancellationToken);
            events.AddRange(batch.Events.Where(item => item.Seq > after).OrderBy(item => item.Seq));
            after = events.Count > 0 ? events[^1].Seq : after;

            if (!batch.HasMore)
            {
                break;
            }
        }

        var operation = await remote.GetAsync(query.OperationId, cancellationToken);
        return new RemoteOperationProgress(operation, events, after);
    }
}

public sealed record CancelRemoteOperation(Guid OperationId);

/// <summary>Pede para cancelar; quem decide quando para é o agente de lá.</summary>
public sealed class CancelRemoteOperationHandler(IRemoteOperationsApi remote)
{
    public Task<RemoteOperationView> HandleAsync(CancelRemoteOperation command, CancellationToken cancellationToken = default) =>
        remote.CancelAsync(command.OperationId, cancellationToken);
}
