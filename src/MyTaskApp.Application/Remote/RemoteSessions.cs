using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>As conversas do Claude no servidor (ADR-091): o agente relata, os controladores leem.</summary>
public interface IRemoteSessionsApi
{
    /// <summary>O relato do agente; o lote vazio é o sinal de vida dele.</summary>
    Task<RemoteAgentSessionAck> ReportSessionsAsync(RemoteAgentSessionBatch batch, CancellationToken cancellationToken);

    Task<RemoteAgentSessionList> ListSessionsAsync(Guid? deviceId, bool openOnly, int? limit, CancellationToken cancellationToken);

    Task<RemoteAgentSessionDetail> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// O relato das conversas sem terminal desta máquina ao servidor (ADR-091),
/// a cada volta do <see cref="RemoteOperationScheduler"/>, que só o agente roda.
/// Manda só o que mudou desde o último relato aceito; sem mudança, o lote
/// vazio vai mesmo assim, como sinal de vida do agente.
/// </summary>
/// <remarks>
/// A versão é o instante da mudança (em ticks), e não um contador guardado:
/// o agente reiniciado relata tudo de novo com versões maiores, sem tabela
/// local. Se o servidor tem uma maior (o relógio voltou), a próxima pula à
/// frente dela. Um servidor antigo (a rota não existe) é deixado em paz por
/// <see cref="OldServerBackoff"/>.
/// </remarks>
public sealed class RemoteSessionPublisher(
    IRemoteSessionsApi api,
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    ILogger<RemoteSessionPublisher> logger)
{
    /// <summary>As conversas relatadas: as ativas e as com prompt neste prazo.</summary>
    public static readonly TimeSpan ReportWindow = TimeSpan.FromDays(7);

    public static readonly TimeSpan OldServerBackoff = TimeSpan.FromHours(1);

    private readonly Dictionary<Guid, string> _reported = [];

    private long _lastVersion;

    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (now < _quietUntil)
        {
            return;
        }

        var sessions = await runner.RunAsync<IAgentSessionRepository, IReadOnlyList<AgentSession>>(
            (repository, token) => repository.ListHeadlessAsync(now - ReportWindow, RemoteLimits.MaxSessionListSize, token),
            cancellationToken);

        var changed = sessions
            .Select(session => (Session: session, Fingerprint: RemoteSessionReports.Fingerprint(session)))
            .Where(item => _reported.GetValueOrDefault(item.Session.Id) != item.Fingerprint)
            .Take(RemoteLimits.MaxSessionsPerBatch)
            .ToList();

        var reports = changed.ToDictionary(item => item.Session.Id, item => (Report: RemoteSessionReports.From(item.Session, NextVersion(now)), item.Fingerprint));

        RemoteAgentSessionAck ack;

        try
        {
            ack = await api.ReportSessionsAsync(new RemoteAgentSessionBatch([.. reports.Values.Select(item => item.Report)]), cancellationToken);
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            _quietUntil = now + OldServerBackoff;
            logger.LogInformation("RemoteSessionsUnsupportedByServer");
            return;
        }

        foreach (var accepted in ack.Sessions)
        {
            if (!reports.TryGetValue(accepted.SessionId, out var sent))
            {
                continue;
            }

            if (accepted.Version == sent.Report.Version)
            {
                _reported[accepted.SessionId] = sent.Fingerprint;
            }
            else if (accepted.Version > _lastVersion)
            {
                // O servidor tem uma versão maior (o relógio desta máquina voltou): a próxima vai além dela.
                _lastVersion = accepted.Version;
            }
        }
    }

    private long NextVersion(DateTimeOffset now) => _lastVersion = Math.Max(now.UtcTicks, _lastVersion + 1);
}

/// <summary>Uma conversa sem terminal desta máquina como o servidor guarda: só ids e estado.</summary>
public static class RemoteSessionReports
{
    public static RemoteAgentSessionReport From(AgentSession session, long version) => new(
        session.Id,
        version,
        session.TaskItemId,
        session.TaskDevelopmentId,
        session.HeadlessProfileId ?? "?",
        session.Model,
        StatusOf(session),
        ActivityOf(session.Activity),
        session.ActivityMessage is { } message ? Clip(SensitiveText.Mask(message)) : null,
        session.RemoteOperationId,
        session.StartedAt,
        session.EndedAt,
        UpdatedAt(session));

    /// <summary>O que, mudando, pede um relato novo.</summary>
    public static string Fingerprint(AgentSession session) => string.Join(
        '|',
        StatusOf(session),
        session.Activity,
        session.ActivityMessage,
        session.RemoteOperationId,
        session.EndedAt?.UtcTicks,
        UpdatedAt(session).UtcTicks);

    internal static RemoteAgentSessionStatus StatusOf(AgentSession session) => session.Status switch
    {
        AgentSessionStatus.Starting => RemoteAgentSessionStatus.Starting,
        AgentSessionStatus.Running => RemoteAgentSessionStatus.Running,
        AgentSessionStatus.Stopped => RemoteAgentSessionStatus.Stopped,
        AgentSessionStatus.Failed => RemoteAgentSessionStatus.Failed,
        _ => RemoteAgentSessionStatus.Idle,
    };

    private static RemoteAgentActivity ActivityOf(AgentActivity activity) => activity switch
    {
        AgentActivity.Working => RemoteAgentActivity.Working,
        AgentActivity.WaitingForUser => RemoteAgentActivity.WaitingForUser,
        AgentActivity.WaitingReview => RemoteAgentActivity.WaitingReview,
        AgentActivity.Failed => RemoteAgentActivity.Failed,
        _ => RemoteAgentActivity.Unknown,
    };

    private static DateTimeOffset UpdatedAt(AgentSession session) =>
        new[] { session.StartedAt, session.PromptStartedAt, session.ActivityChangedAt, session.EndedAt }
            .Where(at => at is not null)
            .Max(at => at!.Value);

    private static string? Clip(string? text) =>
        text is { Length: > RemoteLimits.MaxSessionActivityLength } ? $"{text[..(RemoteLimits.MaxSessionActivityLength - 1)]}…" : text;
}

/// <summary>As conversas de uma máquina (ou de todas), para a lista "Conversas" do desktop e do celular.</summary>
public sealed record ListRemoteSessions(Guid? DeviceId = null, bool OpenOnly = false);

/// <summary>Uma conversa como a lista mostra: o título vem da tarefa sincronizada aqui.</summary>
public sealed record RemoteSessionItem(RemoteAgentSessionView Session, RemoteAgentPresence Agent, string Title, string? Branch, string State)
{
    public bool CanPrompt => RemoteSessionTexts.CanPrompt(Session);

    public bool CanStop => RemoteSessionTexts.CanStop(Session);
}

/// <param name="Unavailable">O servidor não guarda conversas (é anterior a isso): a mensagem para mostrar.</param>
/// <param name="Agents">O estado do agente de cada máquina executora (<see cref="RemoteSessionTexts.Agent"/>), separado do desktop dela.</param>
public sealed record RemoteSessionList(IReadOnlyList<RemoteSessionItem> Items, string? Unavailable = null, IReadOnlyDictionary<Guid, string>? Agents = null)
{
    /// <summary>As máquinas cujo agente relatou antes e parou: os pedidos para elas esperam na fila.</summary>
    public IReadOnlySet<Guid> StoppedAgents { get; init; } = new HashSet<Guid>();

    /// <summary>"agente ligado", "agente parado desde…", ou nulo quando não se sabe.</summary>
    public string? AgentOf(Guid deviceId) => Agents?.GetValueOrDefault(deviceId);

    public bool IsAgentStopped(Guid deviceId) => StoppedAgents.Contains(deviceId);
}

public sealed class ListRemoteSessionsHandler(IRemoteSessionsApi remote, ITaskItemRepository tasks, TimeProvider timeProvider)
{
    public async Task<RemoteSessionList> HandleAsync(ListRemoteSessions query, CancellationToken cancellationToken = default)
    {
        RemoteAgentSessionList list;

        try
        {
            list = await remote.ListSessionsAsync(query.DeviceId, query.OpenOnly, null, cancellationToken);
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            return new RemoteSessionList([], RemoteSessionTexts.ServerTooOld);
        }

        var agents = list.Agents.ToDictionary(agent => agent.DeviceId);
        var items = new List<RemoteSessionItem>(list.Sessions.Count);

        foreach (var session in list.Sessions)
        {
            items.Add(await ItemAsync(tasks, session, agents.GetValueOrDefault(session.DeviceId) ?? new RemoteAgentPresence(session.DeviceId, null), timeProvider, cancellationToken));
        }

        var states = new Dictionary<Guid, string>();
        var stopped = new HashSet<Guid>();

        foreach (var agent in list.Agents)
        {
            if (RemoteSessionTexts.Agent(agent, timeProvider) is { } state)
            {
                states[agent.DeviceId] = state;
            }

            if (agent.AgentSeenAt is not null && !agent.IsOnlineAt(timeProvider.GetUtcNow()))
            {
                stopped.Add(agent.DeviceId);
            }
        }

        return new RemoteSessionList(items, null, states) { StoppedAgents = stopped };
    }

    internal static async Task<RemoteSessionItem> ItemAsync(
        ITaskItemRepository tasks,
        RemoteAgentSessionView session,
        RemoteAgentPresence agent,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var task = await tasks.FindByIdAsync(session.TaskId, cancellationToken);
        var branch = task?.Developments.FirstOrDefault(development => development.Id == session.DevelopmentId)?.Branch;

        return new RemoteSessionItem(
            session,
            agent,
            task?.Title ?? "Tarefa que ainda não chegou aqui",
            branch,
            RemoteSessionTexts.State(session, agent, timeProvider));
    }
}

/// <summary>
/// Até onde a linha do tempo de uma conversa foi lida: o último evento de
/// cada operação, e as que já acabaram e foram lidas até o fim.
/// </summary>
public sealed record RemoteSessionCursor(IReadOnlyDictionary<Guid, long> After, IReadOnlySet<Guid> Done)
{
    public static RemoteSessionCursor Start { get; } = new(new Dictionary<Guid, long>(), new HashSet<Guid>());
}

/// <summary>A conversa e o que chegou na linha do tempo dela desde <paramref name="Cursor"/>.</summary>
public sealed record GetRemoteSessionTimeline(Guid SessionId, RemoteSessionCursor Cursor);

/// <summary>O tipo de cartão de uma entrada da linha do tempo (E7).</summary>
public enum RemoteTimelineKind
{
    /// <summary>O prompt de quem pediu.</summary>
    Prompt,

    /// <summary>O texto do Claude.</summary>
    Claude,

    /// <summary>A ferramenta que o Claude usou.</summary>
    Tool,

    /// <summary>A saída de um comando: do Claude (Bash, PowerShell) ou de um <c>command.run</c>.</summary>
    Output,

    /// <summary>Um pedido de aprovação e, quando chega, a resposta.</summary>
    Approval,

    /// <summary>O andamento: o Claude abrindo, o prompt na fila.</summary>
    Notice,

    /// <summary>O que deu errado no meio, sem parar o prompt.</summary>
    Problem,

    /// <summary>Como o prompt terminou.</summary>
    Outcome,
}

/// <summary>Uma entrada da linha do tempo, para virar cartão (E7).</summary>
/// <param name="Key">O id da aprovação: a resposta completa o cartão do pedido, em vez de abrir outro.</param>
public sealed record RemoteTimelineEntry(RemoteTimelineKind Kind, string Title, string Text, string? Detail = null, string? Key = null);

/// <param name="Lines">As linhas novas, na ordem das operações e dos eventos.</param>
public sealed record RemoteSessionTimeline(RemoteSessionItem Session, IReadOnlyList<RemoteOperationView> Operations, IReadOnlyList<string> Lines, RemoteSessionCursor Cursor)
{
    /// <summary>As mesmas novidades de <see cref="Lines"/>, como entradas para cartões (E7).</summary>
    public IReadOnlyList<RemoteTimelineEntry> Entries { get; init; } = [];

    /// <summary>Uma operação ainda pode trazer linhas: quem acompanha continua lendo.</summary>
    public bool IsLive => Operations.Any(operation => !Cursor.Done.Contains(operation.OperationId));
}

/// <summary>
/// A linha do tempo de uma conversa: os prompts (as operações dela) em ordem,
/// cada um com as suas linhas. Lê a conversa (com o estado das operações)
/// antes dos eventos, como <see cref="GetRemoteOperationProgressHandler"/>:
/// uma operação terminada só fica "lida" quando não sobra página.
/// </summary>
public sealed class GetRemoteSessionTimelineHandler(IRemoteSessionsApi sessions, IRemoteOperationsApi operations, ITaskItemRepository tasks, TimeProvider timeProvider)
{
    public async Task<RemoteSessionTimeline> HandleAsync(GetRemoteSessionTimeline query, CancellationToken cancellationToken = default)
    {
        var detail = await sessions.GetSessionAsync(query.SessionId, cancellationToken);
        var after = new Dictionary<Guid, long>(query.Cursor.After);
        var done = new HashSet<Guid>(query.Cursor.Done);
        var lines = new List<string>();
        var entries = new List<RemoteTimelineEntry>();

        foreach (var operation in detail.Operations)
        {
            if (done.Contains(operation.OperationId))
            {
                continue;
            }

            var cursor = after.GetValueOrDefault(operation.OperationId);
            var hasMore = false;

            for (var page = 0; page < GetRemoteOperationProgressHandler.MaxPages && cursor < operation.LastContiguousSeq; page++)
            {
                var batch = await operations.ListEventsAsync(operation.OperationId, cursor, null, cancellationToken);

                foreach (var item in batch.Events.Where(item => item.Seq > cursor).OrderBy(item => item.Seq))
                {
                    if (RemoteSessionTexts.Line(item) is { } line)
                    {
                        lines.Add(line);
                    }

                    if (RemoteSessionTexts.Entry(item) is { } entry)
                    {
                        RemoteSessionTexts.Merge(entries, entry);
                    }

                    cursor = item.Seq;
                }

                hasMore = batch.HasMore;

                if (!hasMore)
                {
                    break;
                }
            }

            after[operation.OperationId] = cursor;

            if (RemoteOperationLifecycle.IsFinal(operation.Status) && !hasMore && cursor >= operation.LastContiguousSeq)
            {
                done.Add(operation.OperationId);
                lines.Add($"— {operation.ResultSummary ?? RemoteOperationTexts.Status(operation)}");
                entries.Add(new RemoteTimelineEntry(
                    RemoteTimelineKind.Outcome,
                    RemoteOperationTexts.Status(operation),
                    operation.ResultSummary ?? RemoteOperationTexts.Status(operation)));
            }
        }

        var session = await ListRemoteSessionsHandler.ItemAsync(tasks, detail.Session, detail.Agent, timeProvider, cancellationToken);

        return new RemoteSessionTimeline(session, detail.Operations, lines, new RemoteSessionCursor(after, done)) { Entries = entries };
    }
}

/// <summary>
/// As palavras das conversas, as mesmas no desktop e no celular. O estado de
/// execução vem do último relato do agente; sem notícias dele, o estado é
/// desconhecido, e não "terminou".
/// </summary>
public static class RemoteSessionTexts
{
    public const string ServerTooOld = "O servidor ainda não guarda as conversas do Claude. Atualize o servidor.";

    /// <summary>
    /// O agente da máquina, separado do desktop dela: o desktop aberto não diz
    /// que os pedidos rodam. Nulo quando não se sabe (o agente nunca relatou:
    /// é antigo, ou nunca ligou), para não acusar de parado quem só não conta.
    /// </summary>
    public static string? Agent(RemoteAgentPresence? agent, TimeProvider timeProvider)
    {
        if (agent?.AgentSeenAt is not { } seen)
        {
            return null;
        }

        if (agent.IsOnlineAt(timeProvider.GetUtcNow()))
        {
            return "agente ligado";
        }

        var local = seen.ToOffset(timeProvider.LocalTimeZone.GetUtcOffset(seen));
        return string.Create(CultureInfo.InvariantCulture, $"agente parado desde {local:dd/MM HH:mm}: os pedidos esperam na fila");
    }

    public static string State(RemoteAgentSessionView session, RemoteAgentPresence agent, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();

        if (!agent.IsOnlineAt(now) && session.Status is RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running)
        {
            return agent.AgentSeenAt is { } seen
                ? string.Create(CultureInfo.InvariantCulture, $"Sem notícias do agente desde {seen.ToOffset(timeProvider.LocalTimeZone.GetUtcOffset(seen)):HH:mm}; o estado é desconhecido")
                : "Sem notícias do agente; o estado é desconhecido";
        }

        return session.Status switch
        {
            RemoteAgentSessionStatus.Starting => "Abrindo o Claude",
            RemoteAgentSessionStatus.Running when session.Activity == RemoteAgentActivity.WaitingForUser => "Esperando aprovação",
            RemoteAgentSessionStatus.Running => "Respondendo",
            RemoteAgentSessionStatus.Idle when session.Activity == RemoteAgentActivity.Failed => "O último prompt falhou; espera o próximo",
            RemoteAgentSessionStatus.Idle => "Esperando o próximo prompt",
            RemoteAgentSessionStatus.Stopped => "Parada",
            RemoteAgentSessionStatus.Failed => "Não abriu",
            _ => session.Status.ToString(),
        };
    }

    /// <summary>
    /// Mais um prompt: na conversa que espera o próximo e, com o Claude
    /// respondendo, na fila dela (E6). Com o agente parado, o pedido espera na
    /// fila do servidor.
    /// </summary>
    public static bool CanPrompt(RemoteAgentSessionView session) =>
        session.Status is RemoteAgentSessionStatus.Idle or RemoteAgentSessionStatus.Running or RemoteAgentSessionStatus.Starting;

    public static bool CanStop(RemoteAgentSessionView session) =>
        session.Status is not (RemoteAgentSessionStatus.Stopped or RemoteAgentSessionStatus.Failed);

    /// <summary>
    /// A entrada de um evento, para o cartão (E7); nula para os que não
    /// aparecem (estado, resultado). O prompt de quem pediu é "Você".
    /// </summary>
    public static RemoteTimelineEntry? Entry(RemoteOperationEvent item)
    {
        var data = item.Data is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;

        string? Field(string name) =>
            data is { } found && found.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

        return item.Kind switch
        {
            RemoteEventKind.StepStarted when Field("prompt") is { } prompt => new(RemoteTimelineKind.Prompt, "Você", prompt),
            RemoteEventKind.StepStarted when item.Text is { } text => new(RemoteTimelineKind.Notice, "Andamento", text),
            RemoteEventKind.AgentMessage when item.Text is { } text => new(RemoteTimelineKind.Claude, "Claude", text),
            RemoteEventKind.ToolUse when item.Text is { } text => Field("tool") is { } tool
                ? new(RemoteTimelineKind.Tool, $"Usou {tool}", text.StartsWith($"{tool}: ", StringComparison.Ordinal) ? text[(tool.Length + 2)..] : text)
                : new(RemoteTimelineKind.Tool, "Usou uma ferramenta", text),
            RemoteEventKind.StepCompleted when item.Text is { } text && Field("toolUseId") is not null =>
                new(RemoteTimelineKind.Output, $"Saída de {Field("tool") ?? "comando"}", text),
            RemoteEventKind.Output when item.Text is { } text => new(RemoteTimelineKind.Output, "Saída", text),
            RemoteEventKind.Warning when item.Text is { } text => new(RemoteTimelineKind.Problem, "Aviso", text),
            RemoteEventKind.ApprovalRequested when item.Text is { } text =>
                new(RemoteTimelineKind.Approval, "Pediu aprovação", text, "Esperando a resposta.", Field("approvalId")),
            RemoteEventKind.ApprovalResolved when item.Text is { } text =>
                new(RemoteTimelineKind.Approval, "Aprovação", text, text, Field("approvalId")),
            _ => null,
        };
    }

    /// <summary>A resposta de uma aprovação completa o cartão do pedido; o resto entra no fim.</summary>
    public static void Merge(IList<RemoteTimelineEntry> entries, RemoteTimelineEntry entry)
    {
        if (entry is { Kind: RemoteTimelineKind.Approval, Key: { } key }
            && entries.Select((existing, index) => (existing, index)).LastOrDefault(pair => pair.existing.Key == key) is ({ } asked, var at))
        {
            entries[at] = asked with { Detail = entry.Detail };
            return;
        }

        entries.Add(entry);
    }

    /// <summary>
    /// A linha de um evento na linha do tempo: o prompt de quem pediu aparece
    /// como "Você". O resultado não: a operação lida até o fim já fecha com o resumo.
    /// </summary>
    public static string? Line(RemoteOperationEvent item) => item switch
    {
        { Kind: RemoteEventKind.Result } => null,
        { Kind: RemoteEventKind.StepStarted, Data: { ValueKind: JsonValueKind.Object } data }
            when data.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String => $"Você: {prompt.GetString()}",
        _ => RemoteOperationTexts.Line(item),
    };
}
