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
    /// <summary>"agente ligado", "agente parado desde…", ou nulo quando não se sabe.</summary>
    public string? AgentOf(Guid deviceId) => Agents?.GetValueOrDefault(deviceId);
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

        foreach (var agent in list.Agents)
        {
            if (RemoteSessionTexts.Agent(agent, timeProvider) is { } state)
            {
                states[agent.DeviceId] = state;
            }
        }

        return new RemoteSessionList(items, null, states);
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

/// <param name="Lines">As linhas novas, na ordem das operações e dos eventos.</param>
public sealed record RemoteSessionTimeline(RemoteSessionItem Session, IReadOnlyList<RemoteOperationView> Operations, IReadOnlyList<string> Lines, RemoteSessionCursor Cursor)
{
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
            }
        }

        var session = await ListRemoteSessionsHandler.ItemAsync(tasks, detail.Session, detail.Agent, timeProvider, cancellationToken);

        return new RemoteSessionTimeline(session,detail.Operations, lines, new RemoteSessionCursor(after, done));
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

    /// <summary>Mais um prompt: só na conversa que espera o próximo. Com o agente parado, o pedido espera na fila.</summary>
    public static bool CanPrompt(RemoteAgentSessionView session) => session.Status == RemoteAgentSessionStatus.Idle;

    public static bool CanStop(RemoteAgentSessionView session) =>
        session.Status is not (RemoteAgentSessionStatus.Stopped or RemoteAgentSessionStatus.Failed);

    /// <summary>A linha de um evento na linha do tempo: o prompt de quem pediu aparece como "Você".</summary>
    public static string? Line(RemoteOperationEvent item) =>
        item is { Kind: RemoteEventKind.StepStarted, Data: { ValueKind: JsonValueKind.Object } data }
        && data.TryGetProperty("prompt", out var prompt)
        && prompt.ValueKind == JsonValueKind.String
            ? $"Você: {prompt.GetString()}"
            : RemoteOperationTexts.Line(item);
}
