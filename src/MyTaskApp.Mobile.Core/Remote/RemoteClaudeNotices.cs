using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>
/// O aviso do sistema com o app na frente (ADR-103): o Claude de outra
/// máquina passou a esperar alguém, terminou ou falhou. A etiqueta é a mesma
/// da verificação em segundo plano (ADR-096), e o que sai daqui fica anotado
/// para ela: o mesmo pedido não avisa duas vezes.
/// </summary>
/// <remarks>
/// O quadro só lê com o app na frente (<see cref="Composition.MobileLifecycle"/>);
/// em segundo plano, quem avisa é a verificação do sistema.
/// </remarks>
public sealed class RemoteClaudeNotices(
    RemoteClaudeBoard board,
    IUseCaseRunner runner,
    ILocalNotifier notifier,
    RemoteBackgroundCheck background,
    ILogger<RemoteClaudeNotices> logger)
{
    private bool _started;

    /// <summary>Quantos avisos saíram (os testes esperam por eles).</summary>
    public int Shown { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        board.AttentionRaised += OnRaised;
    }

    /// <summary>Avisa um pedido e anota para a verificação em segundo plano.</summary>
    public async Task NotifyAsync(RemoteClaudeState state, CancellationToken cancellationToken)
    {
        var task = await runner.RunAsync<ITaskItemRepository, TaskItem?>(
            (tasks, token) => tasks.FindByIdAsync(state.TaskId, token),
            cancellationToken);

        var conversation = state.Kind == RemoteClaudeKind.Conversation;
        notifier.Show(new LocalNotice(
            conversation ? $"session:{state.SessionId:N}" : $"terminal:{state.SessionId:N}",
            task?.Title ?? "Tarefa",
            RemoteClaudeTexts.Notice(state)));
        Shown++;

        await background.NoteShownAsync(
            conversation ? [] : [state.SessionId],
            conversation && state.Version is { } version ? new Dictionary<Guid, long> { [state.SessionId] = version } : new Dictionary<Guid, long>(),
            cancellationToken);
    }

    private void OnRaised(object? sender, RemoteClaudeAttentionEventArgs e) => _ = NotifyQuietlyAsync(e.State);

    private async Task NotifyQuietlyAsync(RemoteClaudeState state)
    {
        try
        {
            await NotifyAsync(state, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A linha da tarefa mostra do mesmo jeito: o aviso é um extra.
            logger.LogWarning(exception, "MobileRemoteClaudeNoticeFailed");
        }
    }
}
