using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Agents;

/// <summary>As conversas sem terminal abertas de longe nesta máquina depois de <paramref name="Since"/>.</summary>
public sealed record GetRemotelyOpenedSessions(DateTimeOffset Since);

/// <summary>
/// Lê as conversas que o agente desta máquina abriu a pedido de outro
/// dispositivo (ADR-092), já como aviso: a tarefa, o ambiente e quem abriu.
/// </summary>
public sealed class GetRemotelyOpenedSessionsHandler(
    IAgentSessionRepository sessions,
    ITaskItemRepository tasks,
    IAgentCliProviders providers)
{
    /// <summary>Mais do que isso de uma vez é rajada: o resto aparece no card.</summary>
    public const int MaxPerCheck = 5;

    public async Task<IReadOnlyList<AgentAttention>> HandleAsync(GetRemotelyOpenedSessions query, CancellationToken cancellationToken = default)
    {
        var opened = (await sessions.ListHeadlessAsync(query.Since, MaxPerCheck * 4, cancellationToken))
            .Where(session => session.RemoteOrigin is not null && session.StartedAt >= query.Since)
            .OrderBy(session => session.StartedAt)
            .Take(MaxPerCheck)
            .ToList();

        var attentions = new List<AgentAttention>(opened.Count);

        foreach (var session in opened)
        {
            var task = await tasks.FindByIdAsync(session.TaskItemId, cancellationToken);
            var development = task?.Developments.FirstOrDefault(item => item.Id == session.TaskDevelopmentId);

            attentions.Add(new AgentAttention(
                session.Id,
                session.TaskItemId,
                session.TaskDevelopmentId,
                task?.Title ?? "Tarefa",
                providers.Find(session.ProviderId)?.Name ?? session.ProviderId,
                GetTodayBoardHandler.RepositoryName(development?.RepositoryPath),
                development?.Branch,
                session.Activity,
                session.ActivityMessage,
                session.StartedAt,
                OpenedBy: session.RemoteOrigin));
        }

        return attentions;
    }
}

/// <summary>
/// Avisa nesta máquina quando alguém abre de longe um Claude aqui (ADR-092):
/// a conversa roda pelo agente, sem terminal, e sem o aviso ela rodaria
/// escondida. O agente grava e avisa <see cref="DataArea.AgentSessions"/> pelo
/// canal local; daqui sai o aviso do canto, com o botão para a conversa.
/// </summary>
/// <remarks>
/// Só o desktop chama <see cref="Start"/>. Cada conversa é avisada uma vez,
/// e só as abertas depois de o desktop subir: as de antes estão no card.
/// </remarks>
public sealed class RemoteSessionAnnouncer(
    IDataChangeNotifier notifier,
    IUseCaseRunner runner,
    IAgentAttentionPresenter presenter,
    TimeProvider timeProvider,
    ILogger<RemoteSessionAnnouncer> logger)
{
    private readonly HashSet<Guid> _announced = [];

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _since;

    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _since = timeProvider.GetUtcNow();
        notifier.Changed += OnChanged;
    }

    /// <summary>Procura as conversas novas e avisa as que ainda não foram avisadas.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var opened = await runner.RunAsync<GetRemotelyOpenedSessionsHandler, IReadOnlyList<AgentAttention>>(
                (handler, token) => handler.HandleAsync(new GetRemotelyOpenedSessions(_since), token),
                cancellationToken);

            foreach (var attention in opened.Where(attention => _announced.Add(attention.SessionId)))
            {
                await presenter.PresentAsync(attention, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnChanged(DataArea areas)
    {
        if (areas.HasFlag(DataArea.AgentSessions))
        {
            _ = CheckQuietlyAsync();
        }
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            await CheckAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // O card mostra a conversa do mesmo jeito: o aviso é um extra.
            logger.LogWarning(exception, "RemoteSessionAnnounceFailed");
        }
    }
}
