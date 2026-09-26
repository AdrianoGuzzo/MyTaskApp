using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>Um aviso do agente chegou pela porta local (ADR-036).</summary>
/// <param name="Token">O segredo que veio junto: só o processo que o app abriu o conhece.</param>
public sealed record RecordAgentEvent(AgentEvent Event, string? Token);

/// <summary>O que foi feito com o aviso.</summary>
public enum AgentEventOutcome
{
    /// <summary>A atividade da sessão mudou.</summary>
    Applied,

    /// <summary>Válido, mas não mudou nada: repetido, só informativo, ou sessão já encerrada.</summary>
    Ignored,

    /// <summary>Sessão desconhecida, segredo errado ou tarefa que não bate.</summary>
    Rejected,
}

/// <summary>
/// Leva o aviso do agente à sessão que o abriu: confere a associação, muda a
/// atividade, grava, redesenha e — se o agente parou esperando o usuário —
/// avisa.
/// </summary>
/// <remarks>
/// <para>
/// <b>A associação é conferida, não confiada.</b> O id da sessão vem do
/// ambiente que o app montou, mas qualquer processo do computador alcança a
/// porta local. Sem o segredo daquela sessão, o aviso é recusado.
/// </para>
/// <para>
/// <b>O processo continua sendo a verdade do "está aberto"</b> (ADR-030). O
/// aviso muda só a atividade; o fim da sessão vem do fim do processo. Um
/// <c>SessionEnd</c> não encerra nada: o <c>/clear</c> também manda um, e o
/// Claude continua aberto.
/// </para>
/// <para>
/// <b>Não avisa quem já está olhando.</b> Se o terminal do agente é a janela
/// em primeiro plano, o usuário está conversando com ele — cada resposta
/// terminada viraria um aviso a mais em cima da conversa.
/// </para>
/// </remarks>
public sealed class RecordAgentEventHandler(
    IAgentSessionRepository sessions,
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    IAgentSessionWatcher watcher,
    IAgentAttentionPresenter presenter,
    ITerminalWindowManager windows,
    ILogger<RecordAgentEventHandler> logger)
{
    public async Task<AgentEventOutcome> HandleAsync(
        RecordAgentEvent command,
        CancellationToken cancellationToken = default)
    {
        var agentEvent = command.Event;
        var session = await sessions.FindByIdAsync(agentEvent.AgentSessionId, cancellationToken);

        if (!Belongs(agentEvent, session, command.Token))
        {
            logger.LogWarning(
                "AgentEventRejected {AgentSessionId} {TaskId} {SourceEvent}",
                agentEvent.AgentSessionId,
                agentEvent.TaskId,
                agentEvent.SourceEvent);

            return AgentEventOutcome.Rejected;
        }

        logger.LogInformation(
            "AgentEventReceived {TaskId} {AgentSessionId} {SourceEvent} {EventType} {ExternalSessionId} {Metadata}",
            session.TaskItemId,
            session.Id,
            agentEvent.SourceEvent,
            agentEvent.Type,
            agentEvent.ExternalSessionId,
            agentEvent.Metadata);

        if (!session.IsActive)
        {
            return AgentEventOutcome.Ignored;
        }

        WarnIfOutsideTheWorktree(session, agentEvent);

        var previousExternalId = session.ExternalSessionId;
        session.RecordExternalSession(agentEvent.ExternalSessionId);

        var changed = agentEvent.Activity is { } activity
            && session.RecordActivity(activity, agentEvent.Message, agentEvent.ReceivedAt);

        if (!changed && previousExternalId == session.ExternalSessionId)
        {
            return AgentEventOutcome.Ignored;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (!changed)
        {
            return AgentEventOutcome.Ignored;
        }

        logger.LogInformation(
            "AgentActivityChanged {TaskId} {AgentSessionId} {Activity}",
            session.TaskItemId,
            session.Id,
            session.Activity);

        watcher.NotifyChanged(session.TaskItemId);

        await TellTheUserAsync(session, cancellationToken);

        return AgentEventOutcome.Applied;
    }

    private static bool Belongs(AgentEvent agentEvent, [NotNullWhen(true)] AgentSession? session, string? token) =>
        session is { IsMonitored: true }
        && AgentHookToken.Matches(token, session.HookTokenHash)
        && (agentEvent.TaskId is null || agentEvent.TaskId == session.TaskItemId);

    /// <summary>
    /// A pasta não decide a associação (o segredo decide), mas uma pasta fora do
    /// worktree é sinal de algo estranho — vai para o log, e o aviso vale.
    /// </summary>
    private void WarnIfOutsideTheWorktree(AgentSession session, AgentEvent agentEvent)
    {
        if (agentEvent.WorkingDirectory is not { Length: > 0 } directory)
        {
            return;
        }

        var worktree = Normalize(session.WorkingDirectory);
        var actual = Normalize(directory);

        if (actual == worktree || actual.StartsWith(worktree + '/', StringComparison.Ordinal))
        {
            return;
        }

        logger.LogWarning(
            "AgentEventOutsideWorktree {TaskId} {AgentSessionId} {WorkingDirectory}",
            session.TaskItemId,
            session.Id,
            directory);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();

    /// <summary>
    /// Parou esperando o usuário: aviso na tela, a menos que ele já esteja no
    /// terminal. Voltou a trabalhar: o aviso que estava na tela sai.
    /// </summary>
    private async Task TellTheUserAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!session.NeedsAttention)
        {
            await presenter.DismissAsync(session.Id, cancellationToken);
            return;
        }

        if (session.ProcessId is { } processId && await windows.IsInForegroundAsync(processId))
        {
            logger.LogInformation(
                "AgentAttentionSkippedTerminalInFront {TaskId} {AgentSessionId}",
                session.TaskItemId,
                session.Id);
            return;
        }

        var task = await tasks.FindByIdAsync(session.TaskItemId, cancellationToken);
        var development = task?.Developments.FirstOrDefault(item => item.Id == session.TaskDevelopmentId);

        await presenter.PresentAsync(
            new AgentAttention(
                session.Id,
                session.TaskItemId,
                session.TaskDevelopmentId,
                task?.Title ?? "Tarefa",
                providers.Find(session.ProviderId)?.Name ?? session.ProviderId,
                GetTodayBoardHandler.RepositoryName(development?.RepositoryPath),
                development?.Branch,
                session.Activity,
                session.ActivityMessage,
                session.ActivityChangedAt ?? DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
