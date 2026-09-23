using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Quem acompanha os processos das sessões (ADR-029). Os casos de uso avisam
/// por aqui; o monitor é a implementação.
/// </summary>
public interface IAgentSessionWatcher
{
    /// <summary>Passa a vigiar o processo da sessão, se ainda não vigia.</summary>
    void Watch(AgentSessionWatch watch);

    /// <summary>A sessão da tarefa mudou — a lista e a janela da tarefa redesenham.</summary>
    void NotifyChanged(Guid taskId);
}

public sealed record AgentSessionWatch(Guid SessionId, Guid TaskId, int ProcessId, DateTimeOffset ProcessStartedAt)
{
    public static AgentSessionWatch? For(AgentSession session) =>
        session is { Status: AgentSessionStatus.Running, ProcessId: { } processId, ProcessStartedAt: { } startedAt }
            ? new AgentSessionWatch(session.Id, session.TaskItemId, processId, startedAt)
            : null;
}

/// <summary>O agente está instalado neste computador?</summary>
public sealed record DetectAgentCli(string? ProviderId = null);

public sealed class DetectAgentCliHandler(IAgentCliProviders providers)
{
    public async Task<AgentCliStatus> HandleAsync(
        DetectAgentCli query,
        CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(query.ProviderId);
        var detection = await provider.DetectAsync(cancellationToken);

        return new AgentCliStatus(
            provider.Id,
            provider.Name,
            provider.Command,
            detection,
            detection.IsInstalled ? null : provider.InstallGuideFor(CurrentPlatform()));
    }

    internal static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;
}

/// <summary>A sessão mais recente da tarefa, já conferida contra o sistema.</summary>
public sealed record GetTaskAgentSession(Guid TaskId);

/// <summary>
/// Nunca devolve "Em execução" só porque o banco diz: sessão ativa cujo processo
/// sumiu é encerrada e gravada antes de voltar (ADR-029).
/// </summary>
public sealed class GetTaskAgentSessionHandler(
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    IAgentProcessTracker processes,
    IAgentSessionWatcher watcher,
    TimeProvider timeProvider)
{
    public async Task<AgentSessionView?> HandleAsync(
        GetTaskAgentSession query,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindLatestForTaskAsync(query.TaskId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            watcher.NotifyChanged(session.TaskItemId);
        }

        return AgentSessionView.From(session, providers);
    }
}

/// <summary>Abre o agente num terminal, dentro do worktree da tarefa.</summary>
public sealed record StartAgentSession(Guid TaskId, string? ProviderId = null);

/// <summary>
/// O fluxo da ADR-029: worktree pronto → agente instalado → sessão gravada como
/// "iniciando" → terminal aberto → PID gravado → monitor vigiando.
/// </summary>
/// <remarks>
/// <para>
/// A sessão é gravada <b>antes</b> de o terminal abrir: se o app cair no meio,
/// sobra uma sessão sem PID que a reconciliação encerra, e não um terminal
/// órfão sem registro nenhum.
/// </para>
/// <para>
/// Falha ao abrir o terminal não lança: vira uma sessão <see cref="AgentSessionStatus.Failed"/>
/// com o motivo, que a tela mostra. Lança só o que impede de tentar — worktree
/// ausente, agente não instalado, sessão já aberta.
/// </para>
/// </remarks>
public sealed class StartAgentSessionHandler(
    ITaskItemRepository tasks,
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    ITerminalLauncher launcher,
    IAgentProcessTracker processes,
    IAgentSessionWatcher watcher,
    IDirectoryProbe directories,
    TimeProvider timeProvider,
    ILogger<StartAgentSessionHandler> logger)
{
    public async Task<AgentSessionView> HandleAsync(
        StartAgentSession command,
        CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(command.ProviderId);
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        if (task.Development is not { Status: TaskDevelopmentStatus.Ready } development)
        {
            throw new DomainException(
                $"O {provider.Name} só abre com o worktree da tarefa pronto.");
        }

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            throw new DomainException(
                $"A pasta do worktree ({development.WorktreePath}) não existe mais. O {provider.Name} não foi aberto.");
        }

        await EnsureNoActiveSessionAsync(task.Id, provider, cancellationToken);

        var detection = await provider.DetectAsync(cancellationToken);

        if (!detection.IsInstalled || detection.ExecutablePath is null)
        {
            throw new DomainException($"{provider.Name} não encontrado.");
        }

        var launch = provider.CreateLaunch(
            new AgentCliStartContext(task.Id, development.WorktreePath),
            detection);

        var session = AgentSession.Create(
            task.Id,
            provider.Id,
            launch.Executable,
            launch.WorkingDirectory,
            timeProvider.GetUtcNow());

        await sessions.AddAsync(session, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        TerminalLaunchResult result;

        try
        {
            result = await launcher.LaunchAsync(launch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "AgentTerminalLaunchThrew {TaskId} {ProviderId}", task.Id, provider.Id);
            result = TerminalLaunchResult.Failed("Não foi possível abrir o terminal.");
        }

        if (!result.Started)
        {
            session.MarkFailed(result.Error ?? "Não foi possível abrir o terminal.", timeProvider.GetUtcNow());
            await SaveAndNotifyAsync(session, cancellationToken);

            logger.LogWarning(
                "AgentSessionFailedToStart {TaskId} {ProviderId} {Reason}",
                task.Id,
                provider.Id,
                session.FailureReason);

            return AgentSessionView.From(session, providers);
        }

        session.MarkRunning(result.ProcessId, result.ProcessStartedAt);

        // O agente pode sair na hora (versão quebrada, login pendente que fecha
        // a janela). Então a sessão já nasce encerrada, e não "Em execução".
        if (!processes.IsAlive(result.ProcessId, result.ProcessStartedAt))
        {
            session.MarkExited(timeProvider.GetUtcNow());
        }

        await SaveAndNotifyAsync(session, cancellationToken);

        if (AgentSessionWatch.For(session) is { } watch)
        {
            watcher.Watch(watch);
        }

        logger.LogInformation(
            "AgentSessionStarted {TaskId} {SessionId} {ProviderId} {ProcessId} {Status} {WorkingDirectory}",
            task.Id,
            session.Id,
            provider.Id,
            session.ProcessId,
            session.Status,
            session.WorkingDirectory);

        return AgentSessionView.From(session, providers);
    }

    /// <summary>
    /// Uma sessão ativa por tarefa: abrir outra esconderia a primeira, que é
    /// justamente o terminal que o usuário perderia. A que já morreu é
    /// encerrada e gravada aqui, antes da nova — o índice único do banco
    /// recusaria as duas ativas.
    /// </summary>
    private async Task EnsureNoActiveSessionAsync(
        Guid taskId,
        IAgentCliProvider provider,
        CancellationToken cancellationToken)
    {
        var latest = await sessions.FindLatestForTaskAsync(taskId, cancellationToken);

        if (latest is null || !latest.IsActive)
        {
            return;
        }

        if (!AgentSessionReconciler.EndIfGone(latest, processes, timeProvider.GetUtcNow()))
        {
            throw new DomainException(
                $"Esta tarefa já tem um {provider.Name} aberto. Use \"Abrir terminal do agente\" para ir até ele.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveAndNotifyAsync(AgentSession session, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        watcher.NotifyChanged(session.TaskItemId);
    }
}

/// <summary>Traz para a frente o terminal da sessão ativa da tarefa.</summary>
public sealed record FocusAgentSession(Guid TaskId);

/// <summary>
/// Confere o processo antes: se ele acabou, a sessão é encerrada e nada abre —
/// "Abrir terminal" nunca inicia um agente novo nem mira num PID reaproveitado.
/// </summary>
public sealed class FocusAgentSessionHandler(
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    IAgentProcessTracker processes,
    ITerminalWindowManager windows,
    IAgentSessionWatcher watcher,
    TimeProvider timeProvider,
    ILogger<FocusAgentSessionHandler> logger)
{
    public async Task<AgentFocusResult> HandleAsync(
        FocusAgentSession command,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindLatestForTaskAsync(command.TaskId, cancellationToken)
            ?? throw new DomainException("Esta tarefa não tem sessão de agente.");

        if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            watcher.NotifyChanged(session.TaskItemId);
        }

        if (session is not { Status: AgentSessionStatus.Running, ProcessId: { } processId })
        {
            return new AgentFocusResult(AgentSessionView.From(session, providers), false);
        }

        var focused = await windows.FocusAsync(processId);

        if (!focused)
        {
            logger.LogWarning("AgentTerminalWindowNotFound {TaskId} {ProcessId}", session.TaskItemId, processId);
        }

        return new AgentFocusResult(AgentSessionView.From(session, providers), focused);
    }
}

/// <summary>O processo da sessão terminou — avisado pelo monitor.</summary>
public sealed record EndAgentSession(Guid SessionId);

/// <summary>
/// Encerra sem perguntar ao sistema: quem chama já sabe que o processo saiu, e
/// perguntar de novo poderia achar o PID ainda de pé por um instante.
/// </summary>
public sealed class EndAgentSessionHandler(
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentSessionWatcher watcher,
    TimeProvider timeProvider,
    ILogger<EndAgentSessionHandler> logger)
{
    public async Task HandleAsync(EndAgentSession command, CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindByIdAsync(command.SessionId, cancellationToken);

        if (session is not { IsActive: true })
        {
            return;
        }

        session.MarkExited(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AgentSessionExited {TaskId} {SessionId} {ProcessId}",
            session.TaskItemId,
            session.Id,
            session.ProcessId);

        watcher.NotifyChanged(session.TaskItemId);
    }
}

/// <summary>Confere todas as sessões ativas — na abertura do app e de tempos em tempos.</summary>
public sealed record ReconcileAgentSessions;

/// <summary>Quantas continuam vivas e quantas foram encerradas agora.</summary>
public sealed record AgentReconciliation(int Alive, int Ended)
{
    public static readonly AgentReconciliation Nothing = new(0, 0);
}

/// <summary>
/// É o que recupera as sessões depois de o app ser reaberto (ADR-029): processo
/// vivo volta a ser vigiado; processo que sumiu vira "encerrada". Nunca cria
/// sessão.
/// </summary>
public sealed class ReconcileAgentSessionsHandler(
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentProcessTracker processes,
    IAgentSessionWatcher watcher,
    TimeProvider timeProvider,
    ILogger<ReconcileAgentSessionsHandler> logger)
{
    public async Task<AgentReconciliation> HandleAsync(
        ReconcileAgentSessions command,
        CancellationToken cancellationToken = default)
    {
        var active = await sessions.ListActiveAsync(cancellationToken);

        if (active.Count == 0)
        {
            return AgentReconciliation.Nothing;
        }

        var now = timeProvider.GetUtcNow();
        var ended = active.Where(session => AgentSessionReconciler.EndIfGone(session, processes, now)).ToList();

        if (ended.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        foreach (var session in ended)
        {
            logger.LogInformation(
                "AgentSessionFoundEnded {TaskId} {SessionId} {ProcessId}",
                session.TaskItemId,
                session.Id,
                session.ProcessId);

            watcher.NotifyChanged(session.TaskItemId);
        }

        var alive = 0;

        foreach (var session in active)
        {
            if (AgentSessionWatch.For(session) is { } watch)
            {
                watcher.Watch(watch);
                alive++;
            }
        }

        return new AgentReconciliation(alive, ended.Count);
    }
}
