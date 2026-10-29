using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Qual janela mostra o terminal do agente de uma sessão (ADR-098): a janela
/// nativa que o desktop abriu, ou o próprio desktop com o terminal embutido à
/// vista. É ela que pisca e que diz se a pessoa já está olhando.
/// </summary>
public interface IAgentTerminalWindows
{
    /// <summary>O processo da janela; <c>null</c> = nenhuma mostra a sessão agora.</summary>
    int? WindowProcessFor(Guid sessionId);
}

/// <summary>O agente, os testes: nenhuma janela de terminal do agente.</summary>
public sealed class NoAgentTerminalWindows : IAgentTerminalWindows
{
    public static readonly NoAgentTerminalWindows Instance = new();

    public int? WindowProcessFor(Guid sessionId) => null;
}

/// <summary>
/// Chama a pessoa quando o agente para esperando por ela (ADR-037): som, aviso
/// no canto e a janela do terminal piscando — a menos que ela já esteja
/// olhando. Voltou a trabalhar, o aviso sai.
/// </summary>
public sealed class AgentAttentionNotifier(
    ITaskItemRepository tasks,
    IAgentCliProviders providers,
    IAgentAttentionPresenter presenter,
    ITerminalWindowManager windows,
    IAgentTerminalWindows terminalWindows,
    AgentAlertSoundPlayer sounds,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger logger)
{
    public async Task TellAsync(AgentSession session, CancellationToken cancellationToken)
    {
        // No terminal do agente, a janela é a da vista, não a do processo (que não tem janela).
        var window = session.IsAgentTerminal ? terminalWindows.WindowProcessFor(session.Id) : session.ProcessId;

        if (!session.NeedsAttention)
        {
            await presenter.DismissAsync(session.Id, cancellationToken);

            if (window is { } working)
            {
                await windows.StopFlashingAsync(working);
            }

            return;
        }

        if (window is { } front && await windows.IsInForegroundAsync(front))
        {
            logger.LogInformation(
                "AgentAttentionSkippedTerminalInFront {TaskId} {AgentSessionId}",
                session.TaskItemId,
                session.Id);
            return;
        }

        // O aviso do canto pode ser dispensado ou nem caber na pilha; o terminal
        // piscando na barra continua dizendo onde está a pendência.
        if (window is { } waiting)
        {
            await windows.FlashAsync(waiting);
        }

        // Um som por estado (ADR-042): dá para saber, sem olhar, se é uma
        // pergunta ou só o fim da resposta.
        await sounds.PlayAsync(session.Activity, cancellationToken);

        var task = await tasks.FindByIdAsync(session.TaskItemId, cancellationToken);
        var development = task?.Developments.FirstOrDefault(item => item.Id == session.TaskDevelopmentId);

        await presenter.PresentAsync(
            new AgentAttention(
                session.Id,
                session.TaskItemId,
                session.TaskDevelopmentId,
                task?.Title ?? "Tarefa",
                providers.Find(session.ProviderId)?.Name ?? session.ProviderId,
                session.IsConsultation ? "Consulta Inteligente" : GetTodayBoardHandler.RepositoryName(development?.RepositoryPath),
                development?.Branch,
                session.Activity,
                session.ActivityMessage,
                session.ActivityChangedAt ?? timeProvider.GetUtcNow(),
                DeadlineLabelOf(task)),
            cancellationToken);
    }

    /// <summary>
    /// O agente parou esperando você <b>e</b> a entrega está perto: é isso que
    /// torna a pendência relevante (§26). Prazo folgado não entra no aviso.
    /// </summary>
    private string? DeadlineLabelOf(TaskItem? task)
    {
        if (task?.Occurrences
                .Where(occurrence => occurrence.Status is TaskItemStatus.Pending)
                .Select(occurrence => occurrence.Deadline)
                .OfType<TaskDeadline>()
                .MinBy(deadline => (deadline.Date, deadline.Time)) is not { } deadline)
        {
            return null;
        }

        var view = TaskDeadlineView.Describe(deadline, clock, timeProvider.GetUtcNow());

        return view.Severity >= DeadlineSeverity.Attention ? $"Prazo da tarefa: {view.Label}" : null;
    }
}

/// <summary>
/// O agente avisou que uma sessão do terminal dele mudou de atividade
/// (ADR-098): quem tem tela chama a pessoa. O estado vem do banco, não da
/// mensagem — o canal local só diz qual sessão olhar.
/// </summary>
public sealed record ShowAgentAttention(Guid SessionId);

public sealed class ShowAgentAttentionHandler(
    IAgentSessionRepository sessions,
    ITaskItemRepository tasks,
    IAgentCliProviders providers,
    IAgentAttentionPresenter presenter,
    ITerminalWindowManager windows,
    IAgentTerminalWindows terminalWindows,
    AgentAlertSoundPlayer sounds,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<ShowAgentAttentionHandler> logger)
{
    public async Task HandleAsync(ShowAgentAttention command, CancellationToken cancellationToken = default)
    {
        if (await sessions.FindByIdAsync(command.SessionId, cancellationToken) is not { IsAgentTerminal: true } session)
        {
            return;
        }

        await new AgentAttentionNotifier(tasks, providers, presenter, windows, terminalWindows, sounds, clock, timeProvider, logger)
            .TellAsync(session, cancellationToken);
    }
}
