using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// As conversas do Claude sem terminal (ADR-091): o agente de cada máquina
/// relata as dele, e a conta vê a lista e a linha do tempo de cada uma, com
/// as operações (os prompts e a parada). O relato é também o sinal de vida do
/// agente, separado do desktop da mesma instalação.
/// </summary>
public sealed class RemoteAgentSessionUseCases(
    IDeviceRegistry devices,
    IRemoteDeviceStore remote,
    IRemoteAgentSessionStore sessions,
    IRemoteOperationStore operations,
    IRemoteNotifier notifier,
    TimeProvider timeProvider,
    ILogger<RemoteAgentSessionUseCases> logger)
{
    internal const int DefaultListLimit = 50;

    internal const int MaxShortTextLength = 64;

    /// <summary>
    /// O agente da máquina do token relata as conversas dele. A versão menor
    /// ou igual à guardada é ignorada (a resposta diz a guardada); a conversa
    /// de outra máquina é recusada.
    /// </summary>
    public async Task<RemoteAgentSessionAck> ReportAsync(SyncCaller caller, RemoteAgentSessionBatch batch, CancellationToken cancellationToken)
    {
        Validate(batch);

        var state = (await remote.ListAsync(caller.UserId, cancellationToken)).SingleOrDefault(device => device.DeviceId == caller.DeviceId)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de relatar conversas.");

        if (!state.Roles.HasFlag(DeviceRoles.Executor))
        {
            throw new SyncServerException(RemoteErrorCodes.NotAnExecutor, 409, "Só uma máquina que aceita controle remoto relata conversas.");
        }

        var now = timeProvider.GetUtcNow();
        await sessions.TouchAgentAsync(caller.UserId, caller.DeviceId, now, cancellationToken);

        var versions = new List<RemoteAgentSessionVersion>(batch.Sessions.Count);

        foreach (var report in batch.Sessions)
        {
            var view = ToView(caller.DeviceId, report, now);
            var outcome = await sessions.UpsertAsync(caller.UserId, view, cancellationToken);

            if (outcome.Result == RemoteAgentSessionUpsertResult.OtherDevice)
            {
                throw SyncServerException.Forbidden("Essa conversa é de outra máquina.");
            }

            versions.Add(new RemoteAgentSessionVersion(report.SessionId, outcome.Version));

            if (outcome.Result == RemoteAgentSessionUpsertResult.Stale)
            {
                continue;
            }

            if (report.CurrentOperationId is { } operationId)
            {
                await operations.LinkSessionAsync(caller.UserId, caller.DeviceId, operationId, report.SessionId, cancellationToken);
            }

            await notifier.ToAccountAsync(caller.UserId, RemoteHubMessages.SessionChanged, report.SessionId, report.Version);
        }

        if (batch.Sessions.Count > 0)
        {
            logger.LogInformation("RemoteAgentSessionsReported {UserId} {DeviceId} {Count}", caller.UserId, caller.DeviceId, batch.Sessions.Count);
        }

        return new RemoteAgentSessionAck(versions);
    }

    /// <summary>As conversas da conta (de uma máquina, ou só as abertas), as mudadas por último primeiro, e os agentes delas.</summary>
    public async Task<RemoteAgentSessionList> ListAsync(SyncCaller caller, Guid? deviceId, bool openOnly, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? DefaultListLimit, 1, RemoteLimits.MaxSessionListSize);
        var listed = await sessions.ListAsync(caller.UserId, deviceId, openOnly, size, cancellationToken);

        return new RemoteAgentSessionList(await AgentsAsync(caller.UserId, cancellationToken), listed);
    }

    /// <summary>Uma conversa da conta e as operações dela, da mais antiga à mais nova.</summary>
    public async Task<RemoteAgentSessionDetail> GetAsync(SyncCaller caller, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(caller.UserId, sessionId, cancellationToken) ?? throw SyncServerException.NotFound();
        var agent = (await AgentsAsync(caller.UserId, cancellationToken)).SingleOrDefault(item => item.DeviceId == session.DeviceId)
            ?? new RemoteAgentPresence(session.DeviceId, null);
        // Só as pedidas à máquina da conversa: um pedido a outra máquina que cite o mesmo id não entra.
        var timeline = await operations.ListForSessionAsync(caller.UserId, session.DeviceId, sessionId, RemoteLimits.MaxSessionOperations, cancellationToken);

        return new RemoteAgentSessionDetail(session, agent, [.. timeline.Select(RemoteOperationUseCases.ToView)]);
    }

    /// <summary>A varredura: a conversa sem relato há mais que <paramref name="retention"/> sai.</summary>
    public async Task<int> SweepAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var purged = await sessions.PurgeAsync(timeProvider.GetUtcNow() - retention, cancellationToken);

        if (purged > 0)
        {
            logger.LogInformation("RemoteAgentSessionsPurged {Count}", purged);
        }

        return purged;
    }

    /// <summary>Teto, ids e textos: o relato é dado que outras telas mostram.</summary>
    internal static void Validate(RemoteAgentSessionBatch batch)
    {
        if (batch.Sessions.Count > RemoteLimits.MaxSessionsPerBatch)
        {
            throw SyncServerException.BadRequest($"Até {RemoteLimits.MaxSessionsPerBatch} conversas por relato.");
        }

        if (batch.Sessions.Select(report => report.SessionId).Distinct().Count() != batch.Sessions.Count)
        {
            throw SyncServerException.BadRequest("Uma conversa repetida no relato.");
        }

        foreach (var report in batch.Sessions)
        {
            if (report.SessionId == Guid.Empty || report.TaskId == Guid.Empty || report.Version < 1)
            {
                throw SyncServerException.BadRequest("A conversa precisa de id, tarefa e versão a partir de 1.");
            }

            if (!Enum.IsDefined(report.Status) || !Enum.IsDefined(report.Activity))
            {
                throw SyncServerException.BadRequest("Estado de conversa desconhecido.");
            }

            if (!IsShortText(report.ProfileId) || (report.Model is not null && !IsShortText(report.Model)))
            {
                throw SyncServerException.BadRequest($"O perfil e o modelo vão de 1 a {MaxShortTextLength} caracteres, sem caracteres de controle.");
            }
        }
    }

    private async Task<IReadOnlyList<RemoteAgentPresence>> AgentsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var active = (await devices.ListAsync(userId, cancellationToken)).Where(device => !device.IsRevoked).Select(device => device.DeviceId).ToHashSet();

        return [.. (await sessions.ListAgentsAsync(userId, cancellationToken)).Where(agent => active.Contains(agent.DeviceId))];
    }

    private static RemoteAgentSessionView ToView(Guid deviceId, RemoteAgentSessionReport report, DateTimeOffset now) => new(
        report.SessionId,
        deviceId,
        report.Version,
        report.TaskId,
        report.DevelopmentId,
        report.ProfileId,
        report.Model,
        report.Status,
        report.Activity,
        RemoteOperationUseCases.Cap(report.ActivityMessage, RemoteLimits.MaxSessionActivityLength),
        report.CurrentOperationId,
        report.StartedAt.ToUniversalTime(),
        report.EndedAt?.ToUniversalTime(),
        report.UpdatedAt.ToUniversalTime(),
        now);

    private static bool IsShortText(string value) =>
        value.Length is > 0 and <= MaxShortTextLength && !ReceivedText.HasControlOrFormat(value);
}

public enum RemoteAgentSessionUpsertResult
{
    Inserted,
    Updated,

    /// <summary>A guardada é de versão igual ou maior: nada mudou.</summary>
    Stale,

    /// <summary>A conversa é de outra máquina da conta.</summary>
    OtherDevice,
}

public sealed record RemoteAgentSessionUpsert(RemoteAgentSessionUpsertResult Result, long Version);

/// <summary>As conversas relatadas pelos agentes, e quando cada agente deu notícias.</summary>
public interface IRemoteAgentSessionStore
{
    /// <summary>Grava se a versão é maior que a guardada, e só para a máquina dona da conversa.</summary>
    Task<RemoteAgentSessionUpsert> UpsertAsync(Guid userId, RemoteAgentSessionView session, CancellationToken cancellationToken);

    Task<RemoteAgentSessionView?> FindAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>As mudadas por último primeiro; <paramref name="openOnly"/> deixa de fora as paradas e as que falharam.</summary>
    Task<IReadOnlyList<RemoteAgentSessionView>> ListAsync(Guid userId, Guid? deviceId, bool openOnly, int limit, CancellationToken cancellationToken);

    /// <summary>O agente da máquina deu notícias agora.</summary>
    Task TouchAgentAsync(Guid userId, Guid deviceId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>As máquinas executoras registradas da conta, com o último relato do agente de cada uma.</summary>
    Task<IReadOnlyList<RemoteAgentPresence>> ListAgentsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Apaga as conversas sem relato desde antes de <paramref name="reportedBefore"/>.</summary>
    Task<int> PurgeAsync(DateTimeOffset reportedBefore, CancellationToken cancellationToken);
}
