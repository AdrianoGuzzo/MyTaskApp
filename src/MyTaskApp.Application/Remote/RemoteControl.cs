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

    /// <summary>Sem forçar: com alterações não commitadas, a outra máquina recusa e diz quantas.</summary>
    public static SendRemoteOperation RemoveWorktree(Guid deviceId, RemoteEnvironmentView environment) =>
        new(
            deviceId,
            RemoteOperationKinds.WorktreeRemove,
            Payload(new WorktreeRemovalRequest(environment.TaskId, environment.DevelopmentId, Force: false)),
            environment.TaskId);

    /// <summary>O "Preparar em…": a tarefa vai junto, para a outra máquina esperar o sync trazê-la.</summary>
    public static SendRemoteOperation Provision(Guid deviceId, EnvironmentProvisioningRequest request) =>
        new(deviceId, RemoteOperationKinds.WorktreeProvision, Payload(request), request.TaskId, [request.TaskId]);

    /// <summary>Abre uma conversa do Claude no worktree de lá; o perfil é o id do catálogo, e lá se decide o que ele libera.</summary>
    public static SendRemoteOperation StartAgent(Guid deviceId, RemoteEnvironmentView environment, string profileId, string prompt) =>
        new(
            deviceId,
            RemoteOperationKinds.AgentStart,
            Payload(new ClaudeCodeSessionRequest(environment.DevelopmentId, profileId, null, null, new PromptRequest(null, prompt.Trim()))),
            environment.TaskId,
            [environment.TaskId]);

    /// <summary>Mais um prompt na mesma conversa: perfil, modelo e esforço são os dela.</summary>
    public static SendRemoteOperation PromptAgent(Guid deviceId, RemoteEnvironmentView environment, Guid agentSessionId, string prompt) =>
        PromptAgent(deviceId, environment.TaskId, agentSessionId, prompt);

    /// <summary>A mesma, de quem só tem a tarefa da operação anterior (o celular, na tela da operação).</summary>
    public static SendRemoteOperation PromptAgent(Guid deviceId, Guid? taskId, Guid agentSessionId, string prompt) =>
        new(deviceId, RemoteOperationKinds.AgentPrompt, Payload(new PromptRequest(agentSessionId, prompt.Trim())), taskId);

    public static SendRemoteOperation StopAgent(Guid deviceId, RemoteEnvironmentView environment, Guid agentSessionId) =>
        StopAgent(deviceId, environment.TaskId, agentSessionId);

    public static SendRemoteOperation StopAgent(Guid deviceId, Guid? taskId, Guid agentSessionId) =>
        new(deviceId, RemoteOperationKinds.AgentStop, Payload(new AgentStopRequest(agentSessionId)), taskId);

    private static JsonElement Payload<T>(T payload) => JsonSerializer.SerializeToElement(payload, RemoteJson.Options);
}

/// <summary>
/// As palavras da tela "Operação", as mesmas no desktop e no celular: o estado
/// de uma operação e a linha de cada evento.
/// </summary>
public static class RemoteOperationTexts
{
    public static string Status(RemoteOperationView operation) => operation.Status switch
    {
        RemoteOperationStatus.Queued => "Na fila, esperando a outra máquina",
        RemoteOperationStatus.Dispatched => "Entregue à outra máquina",
        RemoteOperationStatus.Accepted => "Aceito, começando",
        RemoteOperationStatus.Running when operation.CancelRequestedAt is not null => "Cancelando…",
        RemoteOperationStatus.Running => "Rodando",
        RemoteOperationStatus.WaitingApproval => "Esperando aprovação",
        RemoteOperationStatus.Succeeded => "Terminou bem",
        RemoteOperationStatus.Failed => "Falhou",
        RemoteOperationStatus.Canceled => "Cancelado",
        RemoteOperationStatus.Rejected => "Recusado pela outra máquina",
        RemoteOperationStatus.Expired => "Expirou sem a outra máquina pegar",
        RemoteOperationStatus.Interrupted => "Interrompido na outra máquina",
        _ => operation.Status.ToString(),
    };

    /// <summary>O perfil de cópia como a lista mostra: de onde para onde, e se a origem é produção.</summary>
    public static string CopyProfile(CatalogCopyProfile profile, IReadOnlyDictionary<Guid, CatalogDatabase> databases)
    {
        var source = databases.GetValueOrDefault(profile.SourceConnectionId);
        var target = databases.GetValueOrDefault(profile.TargetConnectionId);
        var production = source?.Environment is nameof(Domain.DatabaseOperations.DatabaseEnvironment.Production)
            or nameof(Domain.DatabaseOperations.DatabaseEnvironment.CriticalProduction)
            ? ", produção: pede aprovação"
            : string.Empty;

        return $"{profile.Name} ({source?.Name ?? "?"} → {target?.Name ?? "?"}{production})";
    }

    /// <summary>
    /// A linha que o evento vira na saída; nula para os que não aparecem
    /// (estado). O resultado fecha a saída com o resumo: sem ele, ela parava
    /// na última etapa começada, como se ainda rodasse.
    /// </summary>
    public static string? Line(RemoteOperationEvent item) => item.Kind switch
    {
        RemoteEventKind.Output => item.Text,
        RemoteEventKind.AgentMessage => $"Claude: {item.Text}",
        RemoteEventKind.ToolUse => $"» usou {item.Text}",
        RemoteEventKind.Warning => $"» {item.Text}",
        RemoteEventKind.ApprovalRequested => $"» Esperando aprovação: {item.Text}",
        RemoteEventKind.ApprovalResolved => $"» {item.Text}",
        RemoteEventKind.StepStarted when item.Text is not null => $"» {item.Text}",
        RemoteEventKind.Result when item.Text is not null => $"— {item.Text}",
        _ => null,
    };

    /// <summary>
    /// A conversa do Claude que a operação abriu ou continuou, no último evento
    /// que diz: o id dela e se aceita mais um prompt (sem o "canContinue", aceita).
    /// </summary>
    public static (Guid AgentSessionId, bool CanContinue)? Conversation(IEnumerable<RemoteOperationEvent> events)
    {
        (Guid, bool)? found = null;

        foreach (var item in events)
        {
            if (item.Data is { ValueKind: JsonValueKind.Object } data
                && data.TryGetProperty("agentSessionId", out var id)
                && id.TryGetGuid(out var sessionId))
            {
                found = (sessionId, !(data.TryGetProperty("canContinue", out var more) && more.ValueKind == JsonValueKind.False));
            }
        }

        return found;
    }
}

/// <summary>Como vai uma operação pedida daqui, a partir do evento <paramref name="After"/>.</summary>
public sealed record GetRemoteOperationProgress(Guid OperationId, long After);

/// <param name="After">O último evento lido: a próxima leitura parte dele.</param>
/// <param name="HasMore">Ficaram eventos no servidor para a próxima leitura.</param>
public sealed record RemoteOperationProgress(RemoteOperationView Operation, IReadOnlyList<RemoteOperationEvent> Events, long After, bool HasMore = false)
{
    /// <summary>Terminou e não há mais nada a ler: quem acompanha pode parar.</summary>
    public bool IsFinished => RemoteOperationLifecycle.IsFinal(Operation.Status) && !HasMore;
}

/// <summary>
/// O estado e os eventos novos de uma operação. Lê o estado antes dos
/// eventos: o agente manda toda a saída antes do estado final, então o
/// "terminou" lido aqui já tem atrás de si todos os eventos que a leitura
/// seguinte pega. Lido depois, ele poderia chegar com as últimas linhas ainda
/// fora da página.
/// </summary>
public sealed class GetRemoteOperationProgressHandler(IRemoteOperationsApi remote)
{
    /// <summary>Páginas por leitura: uma saída que despeja muito chega em mais de uma volta, sem travar a tela.</summary>
    internal const int MaxPages = 4;

    public async Task<RemoteOperationProgress> HandleAsync(GetRemoteOperationProgress query, CancellationToken cancellationToken = default)
    {
        var operation = await remote.GetAsync(query.OperationId, cancellationToken);
        var events = new List<RemoteOperationEvent>();
        var after = query.After;
        var hasMore = false;

        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await remote.ListEventsAsync(query.OperationId, after, null, cancellationToken);
            events.AddRange(batch.Events.Where(item => item.Seq > after).OrderBy(item => item.Seq));
            after = events.Count > 0 ? events[^1].Seq : after;
            hasMore = batch.HasMore;

            if (!hasMore)
            {
                break;
            }
        }

        return new RemoteOperationProgress(operation, events, after, hasMore);
    }
}

public sealed record CancelRemoteOperation(Guid OperationId);

/// <summary>Pede para cancelar; quem decide quando para é o agente de lá.</summary>
public sealed class CancelRemoteOperationHandler(IRemoteOperationsApi remote)
{
    public Task<RemoteOperationView> HandleAsync(CancelRemoteOperation command, CancellationToken cancellationToken = default) =>
        remote.CancelAsync(command.OperationId, cancellationToken);
}
