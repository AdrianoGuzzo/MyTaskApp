using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O aviso do canto, nesta máquina, quando o Claude de outra máquina passa a
/// pedir alguém (ADR-103): o mesmo aviso do Claude local, com a máquina no
/// título e o botão para seguir. Some quando o pedido acaba lá.
/// </summary>
/// <remarks>
/// Só o desktop chama <see cref="Start"/>; o celular avisa pelo sistema.
/// </remarks>
public sealed class RemoteClaudeAnnouncer(
    RemoteClaudeBoard board,
    IUseCaseRunner runner,
    IAgentAttentionPresenter presenter,
    ILogger<RemoteClaudeAnnouncer> logger)
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        board.AttentionRaised += OnRaised;
        board.AttentionCleared += OnCleared;
    }

    /// <summary>Mostra o aviso de um pedido, com o título da tarefa sincronizada aqui.</summary>
    public async Task AnnounceAsync(RemoteClaudeState state, CancellationToken cancellationToken)
    {
        var task = await runner.RunAsync<ITaskItemRepository, TaskItem?>(
            (tasks, token) => tasks.FindByIdAsync(state.TaskId, token),
            cancellationToken);
        var development = task?.Developments.FirstOrDefault(item => item.Id == state.DevelopmentId);

        await presenter.PresentAsync(
            new AgentAttention(
                state.SessionId,
                state.TaskId,
                state.DevelopmentId,
                task?.Title ?? "Tarefa",
                "Claude Code",
                GetTodayBoardHandler.RepositoryName(development?.RepositoryPath),
                development?.Branch,
                state.LocalActivity,
                state.Message,
                state.AttentionSince ?? DateTimeOffset.MinValue,
                Remote: new RemoteAgentOrigin(state.DeviceId, state.MachineName, state.CanFollow, state.Kind == RemoteClaudeKind.Conversation)),
            cancellationToken);

        await runner.RunAsync<AgentAlertSoundPlayer>(
            (player, token) => player.PlayAsync(state.LocalActivity, token),
            cancellationToken);
    }

    private void OnRaised(object? sender, RemoteClaudeAttentionEventArgs e) => _ = AnnounceQuietlyAsync(e.State);

    private void OnCleared(object? sender, Guid sessionId) => _ = DismissQuietlyAsync(sessionId);

    private async Task AnnounceQuietlyAsync(RemoteClaudeState state)
    {
        try
        {
            await AnnounceAsync(state, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // O selo da tarefa mostra do mesmo jeito: o aviso é um extra.
            logger.LogWarning(exception, "RemoteClaudeAnnounceFailed");
        }
    }

    private async Task DismissQuietlyAsync(Guid sessionId)
    {
        try
        {
            await presenter.DismissAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "RemoteClaudeDismissFailed");
        }
    }
}
