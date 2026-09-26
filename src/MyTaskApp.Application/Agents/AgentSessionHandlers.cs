using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Quem acompanha os processos das sessões (ADR-030). Os casos de uso avisam
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

public sealed class DetectAgentCliHandler(IAgentCliProviders providers, IAgentSettingsStore settings)
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
            detection.IsInstalled ? null : provider.InstallGuideFor(CurrentPlatform()),
            await settings.ArgumentsForAsync(provider, cancellationToken),
            await settings.MonitoringForAsync(provider, cancellationToken));
    }

    internal static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;
}

/// <summary>A sessão mais recente de um ambiente da tarefa (ADR-031), já conferida contra o sistema.</summary>
public sealed record GetTaskAgentSession(Guid TaskId, Guid DevelopmentId);

/// <summary>
/// Nunca devolve "Em execução" só porque o banco diz: sessão ativa cujo processo
/// sumiu é encerrada e gravada antes de voltar (ADR-030).
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
        var session = await sessions.FindLatestForDevelopmentAsync(query.DevelopmentId, cancellationToken);

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

/// <summary>Abre o agente num terminal, dentro do worktree de um ambiente da tarefa.</summary>
/// <param name="Arguments">
/// O texto do campo "Parâmetros": informado, vira o padrão das próximas
/// aberturas; <c>null</c> usa o salvo.
/// </param>
/// <param name="Prompt">O texto livre com que o agente abre; fica gravado no ambiente.</param>
/// <param name="RunDirectly">Com texto: executar direto, em vez de só planejar.</param>
/// <param name="Monitor">
/// Acompanhar pelos hooks do agente (ADR-036). Informado, vira o padrão das
/// próximas aberturas; <c>null</c> usa o salvo.
/// </param>
public sealed record StartAgentSession(
    Guid TaskId,
    Guid DevelopmentId,
    string? ProviderId = null,
    string? Arguments = null,
    string? Prompt = null,
    bool RunDirectly = false,
    bool? Monitor = null);

/// <summary>
/// O fluxo da ADR-030: worktree pronto → agente instalado → sessão gravada como
/// "iniciando" → terminal aberto → PID gravado → monitor vigiando. Com o
/// acompanhamento ligado (ADR-036), a sessão ganha um segredo antes de ser
/// gravada, e o processo nasce sabendo a tarefa, a sessão e o segredo.
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
    IAgentSettingsStore settings,
    IAgentEventEndpoint events,
    TimeProvider timeProvider,
    ILogger<StartAgentSessionHandler> logger)
{
    public async Task<AgentSessionView> HandleAsync(
        StartAgentSession command,
        CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(command.ProviderId);
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        var development = task.GetDevelopment(command.DevelopmentId);

        if (development.Status != TaskDevelopmentStatus.Ready)
        {
            throw new DomainException(
                $"O {provider.Name} só abre com o worktree do ambiente pronto.");
        }

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            throw new DomainException(
                $"A pasta do worktree ({development.WorktreePath}) não existe mais. O {provider.Name} não foi aberto.");
        }

        await EnsureNoActiveSessionAsync(development.Id, provider, cancellationToken);

        var detection = await provider.DetectAsync(cancellationToken);

        if (!detection.IsInstalled || detection.ExecutablePath is null)
        {
            throw new DomainException($"{provider.Name} não encontrado.");
        }

        var argumentsText = await ResolveArgumentsAsync(provider, command.Arguments, cancellationToken);

        // O texto vai junto com a sessão, no mesmo SaveChanges: reaparece no
        // "iniciar novamente" e ao reabrir a tarefa.
        task.SetDevelopmentAgentPrompt(development.Id, command.Prompt);

        // A sessão nasce antes do comando: o id dela vai no ambiente do processo.
        var session = AgentSession.Create(
            task.Id,
            development.Id,
            provider.Id,
            detection.ExecutablePath,
            development.WorktreePath,
            timeProvider.GetUtcNow());

        var (monitoring, monitoringNote) = await PrepareMonitoringAsync(
            provider, session, development, command.Monitor, cancellationToken);

        var launch = provider.CreateLaunch(
            new AgentCliStartContext(
                task.Id,
                development.WorktreePath,
                AgentArguments.Parse(argumentsText),
                development.AgentPrompt,
                command.RunDirectly,
                monitoring),
            detection);

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

            return AgentSessionView.From(session, providers) with { MonitoringNote = monitoringNote };
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
            "AgentSessionStarted {TaskId} {SessionId} {ProviderId} {ProcessId} {Status} {WorkingDirectory} {Arguments} {Monitored}",
            task.Id,
            session.Id,
            provider.Id,
            session.ProcessId,
            session.Status,
            session.WorkingDirectory,
            argumentsText,
            session.IsMonitored);

        return AgentSessionView.From(session, providers) with { MonitoringNote = monitoringNote };
    }

    /// <summary>
    /// Liga o acompanhamento quando dá (ADR-036). Quando não dá — desligado
    /// pelo usuário, porta local fechada, hooks desligados na configuração do
    /// agente —, o agente abre do mesmo jeito, sem avisos, e o motivo volta
    /// para a tela. Acompanhar é um extra: nunca impede de abrir.
    /// </summary>
    private async Task<(AgentMonitoring? Monitoring, string? Note)> PrepareMonitoringAsync(
        IAgentCliProvider provider,
        AgentSession session,
        TaskDevelopment development,
        bool? requested,
        CancellationToken cancellationToken)
    {
        var enabled = await settings.MonitoringForAsync(provider, cancellationToken);

        if (requested is { } choice && choice != enabled)
        {
            await settings.SaveMonitoringAsync(provider.Id, choice, cancellationToken);
            enabled = choice;
        }

        if (!enabled)
        {
            return (null, null);
        }

        if (events.Address is not { } endpoint)
        {
            logger.LogWarning(
                "AgentMonitoringEndpointUnavailable {TaskId} {ProviderId}",
                session.TaskItemId,
                provider.Id);

            return (null, "Aberto sem acompanhamento: o MyTaskApp não conseguiu abrir a porta local de avisos.");
        }

        if (provider.MonitoringUnavailableReason(development.WorktreePath, endpoint) is { } reason)
        {
            logger.LogWarning(
                "AgentMonitoringUnavailable {TaskId} {ProviderId} {Reason}",
                session.TaskItemId,
                provider.Id,
                reason);

            return (null, $"Aberto sem acompanhamento: {reason}");
        }

        var (token, hash) = AgentHookToken.Create();
        session.EnableMonitoring(hash);

        return (
            new AgentMonitoring(
                endpoint,
                AgentMonitoringEnvironment.For(session, development.WorktreePath, development.Branch, token, endpoint)),
            null);
    }

    /// <summary>
    /// O texto que a tela mandou, já aparado, ou o salvo. O que a tela mandou
    /// vira o novo padrão — gravado junto com a sessão, então parâmetro
    /// inválido (recusado pelo <see cref="AgentArguments.Parse"/> antes disso)
    /// nunca é salvo.
    /// </summary>
    private async Task<string> ResolveArgumentsAsync(
        IAgentCliProvider provider,
        string? requested,
        CancellationToken cancellationToken)
    {
        var current = await settings.ArgumentsForAsync(provider, cancellationToken);

        if (requested is null)
        {
            return current;
        }

        var text = requested.Trim();

        AgentArguments.Parse(text);

        if (text != current)
        {
            await settings.SaveArgumentsAsync(provider.Id, text, cancellationToken);
        }

        return text;
    }

    /// <summary>
    /// Uma sessão ativa por ambiente (ADR-031): abrir outra no mesmo worktree
    /// esconderia a primeira, que é justamente o terminal que o usuário
    /// perderia. Outro repositório da tarefa pode ter o seu. A que já morreu é
    /// encerrada e gravada aqui, antes da nova — o índice único do banco
    /// recusaria as duas ativas.
    /// </summary>
    private async Task EnsureNoActiveSessionAsync(
        Guid developmentId,
        IAgentCliProvider provider,
        CancellationToken cancellationToken)
    {
        var latest = await sessions.FindLatestForDevelopmentAsync(developmentId, cancellationToken);

        if (latest is null || !latest.IsActive)
        {
            return;
        }

        if (!AgentSessionReconciler.EndIfGone(latest, processes, timeProvider.GetUtcNow()))
        {
            throw new DomainException(
                $"Este ambiente já tem um {provider.Name} aberto. Use \"Abrir terminal do agente\" para ir até ele.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveAndNotifyAsync(AgentSession session, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        watcher.NotifyChanged(session.TaskItemId);
    }
}

/// <summary>
/// Traz para a frente o terminal da sessão de um ambiente. Sem ambiente, o da
/// sessão ativa mais recente da tarefa — o selo da lista com um agente só.
/// </summary>
public sealed record FocusAgentSession(Guid TaskId, Guid? DevelopmentId = null);

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
        var session = await FindAsync(command, cancellationToken)
            ?? throw new DomainException("Este ambiente não tem sessão de agente.");

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

    private async Task<AgentSession?> FindAsync(FocusAgentSession command, CancellationToken cancellationToken)
    {
        if (command.DevelopmentId is { } developmentId)
        {
            return await sessions.FindLatestForDevelopmentAsync(developmentId, cancellationToken);
        }

        var active = await sessions.ListActiveForTaskAsync(command.TaskId, cancellationToken);

        return active.Count > 0
            ? active[^1]
            : await sessions.FindLatestForTaskAsync(command.TaskId, cancellationToken);
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
    IAgentAttentionPresenter presenter,
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

        // Fechou o agente: o "aguardando você" dele não tem mais a quem levar.
        await presenter.DismissAsync(session.Id, cancellationToken);
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
/// É o que recupera as sessões depois de o app ser reaberto (ADR-030): processo
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
