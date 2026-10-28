using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// Os tetos locais de um prompt (ADR-089, item 1): o pedido pode pedir menos,
/// nunca mais. Ficam no agente, porque é ele quem paga a conta.
/// </summary>
public sealed record HeadlessAgentLimits(int MaxTurns, decimal MaxBudgetUsd)
{
    public static HeadlessAgentLimits Default { get; } = new(MaxTurns: 40, MaxBudgetUsd: 3m);

    public int Turns(int? asked) => asked is > 0 ? Math.Min(asked.Value, MaxTurns) : MaxTurns;

    public decimal Budget(decimal? asked) => asked is > 0 ? Math.Min(asked.Value, MaxBudgetUsd) : MaxBudgetUsd;
}

/// <summary>A conversa e onde ela roda, como estão nesta máquina.</summary>
public sealed record HeadlessTarget(
    Guid TaskId,
    string TaskTitle,
    Guid DevelopmentId,
    string Branch,
    string WorktreePath,
    AgentToolProfile Profile,
    string? Model,
    string? Effort);

/// <summary>
/// Confere os pedidos do Claude sem terminal antes de rodar (4.4, item 6): o
/// ambiente existe aqui, está pronto e é desta máquina; o perfil existe; a
/// conversa existe e pode continuar. O dono das regras é o domínio; aqui só
/// se diz o motivo antes de abrir processo.
/// </summary>
public sealed class HeadlessAgentPlanner(
    ITaskItemRepository tasks,
    IAgentSessionRepository sessions,
    IAgentProcessTracker processes,
    IDirectoryProbe directories,
    HeadlessConversations conversations,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string BusyMessage = "O Claude ainda está respondendo o prompt anterior.";

    public const string StoppedMessage = "A conversa foi parada. Abra outra.";

    /// <summary>A fila da conversa já tem quem espere (E6): um prompt rodando e um esperando, no máximo.</summary>
    public const string QueueFullMessage = "Já há um prompt esperando a vez nesta conversa. Espere o Claude terminar.";

    public const string TerminalOpenMessage = "Há um Claude aberto no terminal deste worktree. Feche-o lá antes de mandar o prompt.";

    public async Task<HeadlessTarget> StartAsync(Guid? taskId, ClaudeCodeSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Prompt.AgentSessionId is not null)
        {
            throw new DomainException("Para continuar uma conversa, mande um prompt nela.");
        }

        var profile = AgentToolProfiles.Find(request.ProfileId)
            ?? throw new DomainException($"Perfil desconhecido nesta máquina: {request.ProfileId}.");
        var (task, development) = await DevelopmentAsync(taskId, request.DevelopmentId, cancellationToken);

        if (await sessions.FindLatestForDevelopmentAsync(development.Id, cancellationToken) is { IsActive: true } session)
        {
            if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            else
            {
                throw new DomainException("Já há um agente aberto neste worktree. Mande o prompt nele, ou pare-o antes.");
            }
        }

        return new HeadlessTarget(task.Id, task.Title, development.Id, development.Branch, development.WorktreePath, profile, request.Model, request.Effort);
    }

    /// <summary>
    /// Confere o próximo prompt da conversa. <paramref name="leased"/> diz que
    /// quem chama já tem a conversa (<see cref="HeadlessConversations.TryBegin"/>).
    /// Sem isso, com um prompt rodando aqui, o novo espera a vez (E6): só a
    /// conversa parada e a fila cheia recusam, e o resto é conferido quando
    /// chegar a vez dele.
    /// </summary>
    /// <remarks>
    /// Sem prompt rodando nesta máquina, a sessão que ainda diz "rodando" é
    /// resto de um agente que caiu (o job do Windows levou o processo junto):
    /// conferido o processo, ela fecha e a conversa segue.
    /// </remarks>
    public async Task<(AgentSession Session, HeadlessTarget Target)> ContinueAsync(
        Guid? taskId,
        Guid agentSessionId,
        bool leased,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(taskId, agentSessionId, cancellationToken);
        var queued = !leased && conversations.IsBusy(session.Id);

        if (queued)
        {
            if (session.Status == AgentSessionStatus.Stopped)
            {
                throw new DomainException(StoppedMessage);
            }

            if (conversations.WaitingFor(session.Id) >= HeadlessConversations.MaxWaiting)
            {
                throw new DomainException(QueueFullMessage);
            }
        }
        else
        {
            if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow(), TimeSpan.Zero))
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            if (!session.CanContinue)
            {
                throw new DomainException(session.Status switch
                {
                    AgentSessionStatus.Stopped => StoppedMessage,
                    AgentSessionStatus.Starting or AgentSessionStatus.Running => BusyMessage,
                    _ => "A conversa não chegou a abrir. Abra outra.",
                });
            }
        }

        var profile = AgentToolProfiles.Find(session.HeadlessProfileId)
            ?? throw new DomainException("O perfil desta conversa não existe mais nesta máquina. Abra outra.");
        var (task, development) = await DevelopmentAsync(taskId, session.TaskDevelopmentId ?? Guid.Empty, cancellationToken);

        // O Claude do card, no terminal, no mesmo worktree: dois mexendo nos mesmos arquivos.
        if (!queued
            && await sessions.FindLatestForDevelopmentAsync(development.Id, cancellationToken) is { IsActive: true } other
            && other.Id != session.Id)
        {
            if (!AgentSessionReconciler.EndIfGone(other, processes, timeProvider.GetUtcNow()))
            {
                throw new DomainException(TerminalOpenMessage);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (session, new HeadlessTarget(task.Id, task.Title, development.Id, development.Branch, development.WorktreePath, profile, session.Model, session.Effort));
    }

    public async Task<AgentSession> SessionAsync(Guid? taskId, Guid agentSessionId, CancellationToken cancellationToken = default)
    {
        var session = await sessions.FindByIdAsync(agentSessionId, cancellationToken);

        if (session is not { IsHeadless: true })
        {
            throw new DomainException("Essa conversa não existe nesta máquina.");
        }

        if (taskId != session.TaskItemId)
        {
            throw new DomainException("O pedido diz uma tarefa e a conversa, outra.");
        }

        return session;
    }

    private async Task<(TaskItem Task, TaskDevelopment Development)> DevelopmentAsync(Guid? taskId, Guid developmentId, CancellationToken cancellationToken)
    {
        var task = await tasks.FindByIdAsync(taskId ?? Guid.Empty, cancellationToken)
            ?? throw new DomainException("A tarefa não existe nesta máquina.");
        var development = task.Developments.FirstOrDefault(item => item.Id == developmentId)
            ?? throw new DomainException("A tarefa não tem esse ambiente nesta máquina.");

        if (development.Status != TaskDevelopmentStatus.Ready)
        {
            throw new DomainException("O worktree desse ambiente não está pronto nesta máquina.");
        }

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            throw new DomainException("A pasta do worktree não existe mais nesta máquina.");
        }

        return (task, development);
    }
}

/// <summary>As gravações da sessão sem terminal, cada uma no seu escopo: o prompt dura minutos, o escopo não.</summary>
public sealed class HeadlessSessionStore(
    IAgentSessionRepository sessions,
    IAgentProcessTracker processes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Guid> OpenAsync(HeadlessTarget target, string providerId, string command, Guid operationId, CancellationToken cancellationToken)
    {
        // De novo aqui: dois agent.start juntos passam os dois pela conferência.
        if (await sessions.FindLatestForDevelopmentAsync(target.DevelopmentId, cancellationToken) is { IsActive: true })
        {
            throw new DomainException("Já há um agente aberto neste worktree. Mande o prompt nele, ou pare-o antes.");
        }

        var session = AgentSession.CreateHeadless(
            target.TaskId,
            target.DevelopmentId,
            providerId,
            command,
            target.WorktreePath,
            Guid.CreateVersion7(),
            operationId,
            target.Profile.Id,
            target.Model,
            target.Effort,
            timeProvider.GetUtcNow());

        await sessions.AddAsync(session, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task ContinueAsync(Guid sessionId, Guid operationId, CancellationToken cancellationToken)
    {
        var session = await sessions.FindByIdAsync(sessionId, cancellationToken)
            ?? throw new DomainException("A conversa sumiu desta máquina.");

        // De novo aqui: o terminal pode ter aberto entre a conferência e agora.
        if (session.TaskDevelopmentId is { } developmentId
            && await sessions.FindLatestForDevelopmentAsync(developmentId, cancellationToken) is { IsActive: true } other
            && other.Id != sessionId)
        {
            throw new DomainException(HeadlessAgentPlanner.TerminalOpenMessage);
        }

        session.ContinueWith(operationId, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>O processo abriu. Se a conversa foi parada nesse meio-tempo, fica parada: o processo é encerrado por quem parou.</summary>
    public Task RunningAsync(Guid sessionId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken) =>
        ChangeAsync(
            sessionId,
            session =>
            {
                if (session.Status != AgentSessionStatus.Stopped)
                {
                    session.MarkRunning(processId, processStartedAt);
                }
            },
            cancellationToken);

    /// <summary>A conversa foi parada antes de o prompt começar.</summary>
    public async Task<bool> IsStoppedAsync(Guid sessionId, CancellationToken cancellationToken) =>
        (await sessions.FindByIdAsync(sessionId, cancellationToken))?.Status == AgentSessionStatus.Stopped;

    /// <summary>
    /// Ao subir o agente: a conversa que dizia "respondendo" quando ele caiu não
    /// tem mais processo (o job do Windows levou junto). Ela fecha como falha e
    /// volta a receber prompts; a que nem abriu o Claude falha de vez.
    /// Devolve quantas.
    /// </summary>
    public async Task<int> RecoverAsync(Func<Guid, bool> isBusy, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var stale = (await sessions.ListActiveAsync(cancellationToken))
            .Where(session => session.IsHeadless && !isBusy(session.Id) && AgentSessionReconciler.IsGone(session, processes, now, TimeSpan.Zero))
            .ToList();

        foreach (var session in stale)
        {
            if (session is { Status: AgentSessionStatus.Starting, ProcessId: null } && session.PromptStartedAt == session.StartedAt)
            {
                session.MarkFailed("O agente parou antes de o Claude abrir.", now);
            }
            else
            {
                session.EndHeadlessPrompt(AgentActivity.Failed, "O agente parou no meio do prompt.", now);
            }
        }

        if (stale.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }

    public Task ActivityAsync(Guid sessionId, AgentActivity activity, string? message, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, session => session.RecordActivity(activity, message, timeProvider.GetUtcNow()), cancellationToken);

    /// <summary>
    /// O prompt acabou: a atividade final e, se ninguém parou a conversa, o fim
    /// do processo. Só a conversa que nem abriu falha; a que já respondeu antes
    /// continua recebendo prompts. Devolve se ela ainda recebe.
    /// </summary>
    public async Task<bool> EndPromptAsync(Guid sessionId, bool firstPrompt, AgentActivity activity, string? message, CancellationToken cancellationToken)
    {
        var canContinue = false;
        await ChangeAsync(
            sessionId,
            session =>
            {
                var now = timeProvider.GetUtcNow();

                if (firstPrompt && session.Status == AgentSessionStatus.Starting)
                {
                    session.MarkFailed(message ?? "O Claude não abriu.", now);
                    canContinue = false;
                    return;
                }

                session.EndHeadlessPrompt(activity, message, now);
                canContinue = session.CanContinue;
            },
            cancellationToken);

        return canContinue;
    }

    public Task StopAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, session => session.MarkStopped(timeProvider.GetUtcNow()), cancellationToken);

    private async Task ChangeAsync(Guid sessionId, Action<AgentSession> change, CancellationToken cancellationToken)
    {
        var session = await sessions.FindByIdAsync(sessionId, cancellationToken)
            ?? throw new DomainException("A conversa sumiu desta máquina.");
        change(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Os prompts rodando nesta máquina, por conversa: é por aqui que o
/// <c>agent.stop</c> de uma operação encerra o processo que outra abriu. E o
/// "permitir sempre nesta sessão" de cada conversa, que vive só na memória:
/// o agente reiniciado pergunta de novo.
/// </summary>
public sealed class HeadlessConversations
{
    /// <summary>
    /// Quantos prompts esperam a vez numa conversa (E6). Um: a próxima
    /// instrução enquanto o Claude responde. Cada um que espera ocupa uma das
    /// operações simultâneas do agente (<see cref="RemoteDispatchLimits.MaxConcurrent"/>).
    /// </summary>
    public const int MaxWaiting = 1;

    private readonly Lock _gate = new();

    private readonly Dictionary<Guid, HeadlessPromptLease> _running = [];

    private readonly Dictionary<Guid, LinkedList<TaskCompletionSource<HeadlessPromptLease?>>> _waiting = [];

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _remembered = new();

    /// <summary>
    /// Reserva a conversa para um prompt, antes de qualquer gravação: dois
    /// <c>--resume</c> na mesma conversa ao mesmo tempo misturariam as duas.
    /// Nulo se outro prompt já a tem.
    /// </summary>
    public HeadlessPromptLease? TryBegin(Guid sessionId)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(sessionId))
            {
                return null;
            }

            var lease = new HeadlessPromptLease(this, sessionId);
            _running[sessionId] = lease;
            return lease;
        }
    }

    /// <summary>
    /// A conversa para este prompt, na ordem em que chegaram (E6): na hora, se
    /// está livre; senão, quando o prompt da frente soltar. Nulo: a conversa
    /// foi parada enquanto ele esperava.
    /// </summary>
    /// <exception cref="DomainException">A fila da conversa está cheia.</exception>
    public async Task<HeadlessPromptLease?> WaitAsync(Guid sessionId, Func<Task> waiting, CancellationToken cancellationToken)
    {
        TaskCompletionSource<HeadlessPromptLease?> turn;
        LinkedListNode<TaskCompletionSource<HeadlessPromptLease?>> node;

        lock (_gate)
        {
            if (!_running.ContainsKey(sessionId))
            {
                var lease = new HeadlessPromptLease(this, sessionId);
                _running[sessionId] = lease;
                return lease;
            }

            if (!_waiting.TryGetValue(sessionId, out var queue))
            {
                _waiting[sessionId] = queue = new LinkedList<TaskCompletionSource<HeadlessPromptLease?>>();
            }

            if (queue.Count >= MaxWaiting)
            {
                throw new DomainException(HeadlessAgentPlanner.QueueFullMessage);
            }

            turn = new TaskCompletionSource<HeadlessPromptLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = queue.AddLast(turn);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (node.List is { } list)
                {
                    list.Remove(node);
                }
            }

            turn.TrySetCanceled(cancellationToken);
        });

        try
        {
            await waiting();
        }
        catch
        {
            // Sem o aviso, o prompt não espera: a vez que talvez já tenha chegado passa adiante.
            lock (_gate)
            {
                if (node.List is { } list)
                {
                    list.Remove(node);
                }
            }

            if (!turn.TrySetCanceled() && turn.Task.IsCompletedSuccessfully)
            {
                turn.Task.Result?.Dispose();
            }

            throw;
        }

        return await turn.Task;
    }

    public bool IsBusy(Guid sessionId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(sessionId);
        }
    }

    /// <summary>Quantos prompts esperam a vez na conversa.</summary>
    public int WaitingFor(Guid sessionId)
    {
        lock (_gate)
        {
            return _waiting.TryGetValue(sessionId, out var queue) ? queue.Count : 0;
        }
    }

    /// <summary>
    /// Encerra o prompt da conversa, se houver um aqui (rodando ou prestes a
    /// abrir o processo), e dispensa os que esperavam a vez.
    /// </summary>
    public bool Stop(Guid sessionId)
    {
        HeadlessPromptLease? running;
        List<TaskCompletionSource<HeadlessPromptLease?>> dismissed;

        lock (_gate)
        {
            running = _running.GetValueOrDefault(sessionId);
            dismissed = _waiting.Remove(sessionId, out var queue) ? [.. queue] : [];
        }

        foreach (var turn in dismissed)
        {
            turn.TrySetResult(null);
        }

        return running?.Cancel() == true;
    }

    /// <summary>O prompt soltou a conversa: a vez passa ao primeiro que espera, se houver.</summary>
    internal void End(HeadlessPromptLease lease)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(lease.SessionId, out var current) || current != lease)
            {
                return;
            }

            _running.Remove(lease.SessionId);

            if (!_waiting.TryGetValue(lease.SessionId, out var queue))
            {
                return;
            }

            while (queue.First is { } first)
            {
                queue.RemoveFirst();
                var next = new HeadlessPromptLease(this, lease.SessionId);

                if (first.Value.TrySetResult(next))
                {
                    _running[lease.SessionId] = next;
                    break;
                }

                next.Dispose();
            }

            if (queue.Count == 0)
            {
                _waiting.Remove(lease.SessionId);
            }
        }
    }

    public bool IsRemembered(Guid sessionId, string tool) =>
        _remembered.TryGetValue(sessionId, out var tools) && tools.ContainsKey(tool);

    public void Remember(Guid sessionId, string tool) =>
        _remembered.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal))[tool] = true;

    public void Forget(Guid sessionId) => _remembered.TryRemove(sessionId, out _);
}

/// <summary>Um prompt com a conversa nas mãos: só ele roda nela até soltar, e o <c>agent.stop</c> o encerra por aqui.</summary>
public sealed class HeadlessPromptLease : IDisposable
{
    private readonly HeadlessConversations _owner;

    private readonly CancellationTokenSource _stop = new();

    internal HeadlessPromptLease(HeadlessConversations owner, Guid sessionId)
    {
        _owner = owner;
        SessionId = sessionId;
    }

    public Guid SessionId { get; }

    /// <summary>Cancelado quando a conversa é parada.</summary>
    public CancellationToken Stopping => _stop.Token;

    public void Dispose()
    {
        _owner.End(this);
        _stop.Dispose();
    }

    internal bool Cancel()
    {
        try
        {
            _stop.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Ao subir o agente (chamada pelo <see cref="RemoteOperationDispatcher.RecoverAsync"/>),
/// antes de buscar a fila: as conversas que ficaram "respondendo" quando ele caiu.
/// </summary>
public sealed class HeadlessSessionRecovery(IUseCaseRunner runner, HeadlessConversations conversations, ILogger<HeadlessSessionRecovery> logger)
    : IRemoteStartupRecovery
{
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = await runner.RunAsync<HeadlessSessionStore, int>(
            (store, token) => store.RecoverAsync(conversations.IsBusy, token),
            cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning("HeadlessSessionsRecovered {Count}", recovered);
        }
    }
}

/// <summary>
/// Um prompt do Claude sem terminal (ADR-089), de ponta a ponta: a linha de
/// comando pelo provider, o processo pelo runner, a saída em eventos da
/// operação, a ferramenta fora do perfil virando aprovação, e a sessão em dia
/// no banco. Os três executores (<c>agent.start</c>, <c>agent.prompt</c>,
/// <c>agent.stop</c>) passam por aqui.
/// </summary>
public sealed class HeadlessPromptRunner(
    IUseCaseRunner runner,
    IHeadlessAgentProvider provider,
    IHeadlessAgentRunner processes,
    IEnumerable<IHeadlessApprovalGateway> gateways,
    HeadlessConversations conversations,
    HeadlessAgentLimits limits,
    ILogger<HeadlessPromptRunner> logger)
{
    /// <summary>O provider instalado e com a versão que serve; senão, a recusa com o motivo.</summary>
    public async Task<(CliDetectionResult? Detection, RemoteRefusal? Refusal)> DetectAsync(CancellationToken cancellationToken)
    {
        var detection = await provider.DetectAsync(cancellationToken);
        return provider.HeadlessUnavailableReason(detection) is { } reason
            ? (null, new RemoteRefusal(RemoteErrorCodes.PolicyRefused, reason))
            : (detection, null);
    }

    public string ProviderId => provider.Id;

    /// <inheritdoc cref="HeadlessConversations.TryBegin"/>
    public HeadlessPromptLease? TryBegin(Guid sessionId) => conversations.TryBegin(sessionId);

    /// <inheritdoc cref="HeadlessConversations.WaitAsync"/>
    public Task<HeadlessPromptLease?> WaitAsync(Guid sessionId, Func<Task> waiting, CancellationToken cancellationToken) =>
        conversations.WaitAsync(sessionId, waiting, cancellationToken);

    public async Task<RemoteOperationResult> RunAsync(
        RemoteExecution execution,
        HeadlessPromptLease lease,
        HeadlessTarget target,
        CliDetectionResult detection,
        PromptRequest prompt,
        bool resume,
        CancellationToken cancellationToken)
    {
        var sessionId = lease.SessionId;
        var conversation = await runner.RunAsync<IAgentSessionRepository, Guid>(
            async (sessions, token) => Guid.Parse((await sessions.FindByIdAsync(sessionId, token))!.ExternalSessionId!),
            cancellationToken);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Stopping);

        // Parada antes de o processo abrir (o agent.stop chegou entre a conversa abrir e agora): nem abre.
        if (stop.IsCancellationRequested
            || await runner.RunAsync<HeadlessSessionStore, bool>((store, token) => store.IsStoppedAsync(sessionId, token), CancellationToken.None))
        {
            var canceled = cancellationToken.IsCancellationRequested;
            var data = await EndAsync(sessionId, !resume, AgentActivity.WaitingReview, canceled ? "Cancelado." : "Parado a pedido.", null);
            return new RemoteOperationResult(false, canceled ? "Cancelado antes de o Claude abrir." : "O Claude foi parado.", null, data);
        }

        var activity = AgentActivity.Working;
        HeadlessResult? result = null;

        // O nome de cada ferramenta pelo id do uso: o resultado só traz o id.
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);

        async Task SetActivityAsync(AgentActivity next, string? message)
        {
            if (next == activity)
            {
                return;
            }

            activity = next;
            await Store((store, token) => store.ActivityAsync(sessionId, next, message, token));
        }

        var gateway = gateways.FirstOrDefault();
        await using var channel = gateway is null
            ? null
            : await gateway.OpenAsync(
                async (request, token) =>
                {
                    ToolPermissionDecision decision;

                    if (request.ToolName == HeadlessQuestions.Tool)
                    {
                        await SetActivityAsync(AgentActivity.WaitingForUser, "O Claude pergunta.");
                        decision = await AskAsync(execution, sessionId, request.Input, request.ToolUseId, token);
                    }
                    else
                    {
                        var summary = SensitiveText.Mask(provider.Summarize(request.ToolName, request.Input));
                        await SetActivityAsync(AgentActivity.WaitingForUser, $"Aprovar {request.ToolName}?");
                        decision = await DecideAsync(execution, sessionId, request.ToolName, request.Input, summary, request.ToolUseId, token);
                    }

                    await SetActivityAsync(AgentActivity.Working, null);
                    return decision;
                },
                cancellationToken);

        var launch = provider.CreateHeadlessLaunch(
            new HeadlessAgentRequest(
                target.WorktreePath,
                conversation,
                resume,
                target.Profile,
                target.Model,
                target.Effort,
                limits.Turns(prompt.MaxTurns),
                limits.Budget(prompt.MaxBudgetUsd),
                channel?.Access,
                []),
            detection);

        // O id da conversa já no primeiro evento: quem pediu pode parar o primeiro prompt antes do fim.
        // E o começo do prompt, mascarado: a linha do tempo da conversa mostra "Você: …" (ADR-091).
        await execution.EmitAsync(
            RemoteEventKind.StepStarted,
            $"Claude em {target.Branch} · perfil {target.Profile.Name}.",
            "agent",
            Data(sessionId, null, canContinue: true, Preview(prompt.Text)),
            cancellationToken);

        HeadlessRunResult run;

        try
        {
            run = await processes.RunAsync(
                launch,
                prompt.Text,
                (processId, startedAt) => Store((store, token) => store.RunningAsync(sessionId, processId, startedAt, token)),
                async line =>
                {
                    foreach (var item in provider.ReadEvents(line))
                    {
                        switch (item)
                        {
                            case HeadlessText text:
                                await SetActivityAsync(AgentActivity.Working, null);
                                await execution.EmitAsync(RemoteEventKind.AgentMessage, Clip(SensitiveText.Mask(text.Text)), "agent", cancellationToken: CancellationToken.None);
                                break;

                            case HeadlessToolUse use:
                                await SetActivityAsync(AgentActivity.Working, null);
                                tools[use.ToolUseId] = use.Name;
                                await execution.EmitAsync(
                                    RemoteEventKind.ToolUse,
                                    Clip(SensitiveText.Mask($"{use.Name}: {use.Summary}")),
                                    "agent",
                                    JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["tool"] = use.Name, ["toolUseId"] = use.ToolUseId }, RemoteJson.Options),
                                    CancellationToken.None);
                                break;

                            case HeadlessToolResult { IsError: true } failed:
                                await execution.EmitAsync(RemoteEventKind.Warning, Clip(SensitiveText.Mask(failed.Summary)), "agent", cancellationToken: CancellationToken.None);
                                break;

                            // A saída dos comandos (os testes, o build) vai para a linha do tempo (E7). A dos
                            // outros (o conteúdo dos arquivos lidos) fica aqui: o servidor não precisa do código.
                            case HeadlessToolResult done when tools.GetValueOrDefault(done.ToolUseId) is { } tool && HeadlessTimeline.ShowsOutput(tool):
                                await execution.EmitAsync(
                                    RemoteEventKind.StepCompleted,
                                    Clip(SensitiveText.Mask(done.Summary)),
                                    "agent",
                                    JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["tool"] = tool, ["toolUseId"] = done.ToolUseId }, RemoteJson.Options),
                                    CancellationToken.None);
                                break;

                            case HeadlessResult end:
                                result = end;
                                break;
                        }
                    }
                },
                stop.Token);
        }
        catch (CommandStartException exception)
        {
            var data = await EndAsync(sessionId, !resume, AgentActivity.Failed, exception.Message, null);
            return new RemoteOperationResult(false, exception.Message, RemoteErrorCodes.ExecutionFailed, data);
        }

        if (run.Canceled)
        {
            var stopped = !cancellationToken.IsCancellationRequested;

            // Parado por agent.stop: a sessão já está Stopped, e o fim do processo não desfaz isso.
            var data = await EndAsync(sessionId, !resume, AgentActivity.WaitingReview, stopped ? "Parado a pedido." : "Cancelado.", result);
            return new RemoteOperationResult(false, stopped ? "O Claude foi parado." : "Cancelado; a conversa continua.", null, data);
        }

        if (result is null)
        {
            logger.LogWarning("HeadlessAgentWithoutResult {ExitCode} {Error}", run.ExitCode, SensitiveText.Mask(Tail(run.StandardError)));
            var data = await EndAsync(sessionId, !resume, AgentActivity.Failed, "O Claude saiu sem terminar a resposta.", null);
            return new RemoteOperationResult(false, $"O Claude saiu sem terminar a resposta (código {run.ExitCode}).", RemoteErrorCodes.ExecutionFailed, data);
        }

        if (result.IsError)
        {
            var reason = Failure(result);
            var data = await EndAsync(sessionId, !resume, AgentActivity.Failed, reason, result);
            return new RemoteOperationResult(false, reason, RemoteErrorCodes.ExecutionFailed, data);
        }

        return RemoteOperationResult.Success(Summary(result), await EndAsync(sessionId, !resume, AgentActivity.WaitingReview, null, result));
    }

    private Task<JsonElement> EndAsync(Guid sessionId, bool firstPrompt, AgentActivity activity, string? message, HeadlessResult? result) =>
        runner.RunAsync<HeadlessSessionStore, JsonElement>(
            async (store, token) => Data(sessionId, result, await store.EndPromptAsync(sessionId, firstPrompt, activity, message, token)),
            CancellationToken.None);

    /// <summary>
    /// A ferramenta fora do perfil: lembrada nesta conversa, ou perguntada a
    /// quem pediu. O que se lembra é o escopo de <see cref="HeadlessToolScope"/>
    /// (no shell, o comando exato), e não a ferramenta inteira.
    /// </summary>
    private async Task<ToolPermissionDecision> DecideAsync(
        RemoteExecution execution,
        Guid sessionId,
        string tool,
        JsonElement input,
        string summary,
        string? toolUseId,
        CancellationToken cancellationToken)
    {
        var rememberKey = HeadlessToolScope.RememberKey(tool, input);

        if (rememberKey is not null && conversations.IsRemembered(sessionId, rememberKey))
        {
            return ToolPermissionDecision.Allow();
        }

        var approval = await execution.RequestApprovalAsync(
            new RemoteApprovalAsk(
                ApprovalKind.ToolUse,
                Clip(SensitiveText.Mask($"O Claude quer usar {tool}: {summary}")),
                JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["agentSessionId"] = sessionId,
                        ["tool"] = tool,
                        ["input"] = Clip(summary),
                        ["toolUseId"] = toolUseId,
                        ["canRemember"] = rememberKey is not null,
                        ["rememberAs"] = rememberKey is null ? null : Clip(SensitiveText.Mask(rememberKey)),
                    },
                    RemoteJson.Options)),
            cancellationToken);

        if (approval.IsAllowed && approval.RememberForThisSession && rememberKey is not null)
        {
            conversations.Remember(sessionId, rememberKey);
        }

        return approval.IsAllowed
            ? ToolPermissionDecision.Allow()
            : ToolPermissionDecision.Deny(approval.Outcome == RemoteApprovalOutcome.Expired
                ? "Ninguém respondeu a tempo: negado. Siga sem essa ferramenta."
                : $"Negado por {approval.ControllerNickname ?? execution.ControllerNickname}. Siga sem essa ferramenta.");
    }

    /// <summary>
    /// A pergunta do Claude (ADR-093): vai a quem pediu como uma aprovação de
    /// ferramenta, com as opções nos detalhes, mascaradas. Permitir com as
    /// respostas devolve ao Claude a entrada com elas; sem resposta que sirva,
    /// ele segue pelo próprio julgamento, e diz qual.
    /// </summary>
    private static async Task<ToolPermissionDecision> AskAsync(
        RemoteExecution execution,
        Guid sessionId,
        JsonElement input,
        string? toolUseId,
        CancellationToken cancellationToken)
    {
        if (HeadlessQuestions.Read(input) is not { } asked)
        {
            return ToolPermissionDecision.Deny("A pergunta não veio na forma esperada. Siga pelo seu julgamento e diga o que escolheu.");
        }

        var shown = HeadlessQuestions.Shown(asked, text => SensitiveText.Mask(text));
        var approval = await execution.RequestApprovalAsync(
            new RemoteApprovalAsk(
                ApprovalKind.ToolUse,
                Clip(asked.Count == 1 ? $"O Claude pergunta: {shown[0].Text}" : $"O Claude faz {asked.Count} perguntas: {shown[0].Text}"),
                JsonSerializer.SerializeToElement(HeadlessQuestions.Details(sessionId, toolUseId, shown), RemoteJson.Options)),
            cancellationToken);

        if (approval.Outcome == RemoteApprovalOutcome.Expired)
        {
            return ToolPermissionDecision.Deny("Ninguém respondeu a tempo. Siga pelo seu julgamento e diga o que escolheu.");
        }

        if (!approval.IsAllowed)
        {
            return ToolPermissionDecision.Deny($"{approval.ControllerNickname ?? execution.ControllerNickname} preferiu não responder. Siga pelo seu julgamento e diga o que escolheu.");
        }

        // Um controlador antigo permite sem as respostas: vale como "decida você".
        return HeadlessQuestions.ReadAnswers(approval.Answers, asked.Count) is { } answers
            ? ToolPermissionDecision.Allow(HeadlessQuestions.Answer(input, HeadlessQuestions.ForClaude(asked, shown, answers)))
            : ToolPermissionDecision.Deny("A pergunta foi aceita sem escolher uma opção. Siga pelo seu julgamento e diga o que escolheu.");
    }

    private Task Store(Func<HeadlessSessionStore, CancellationToken, Task> change) =>
        runner.RunAsync<HeadlessSessionStore, bool>(
            async (store, token) =>
            {
                await change(store, token);
                return true;
            },
            CancellationToken.None);

    /// <summary>
    /// O id da conversa vai no resultado: é por ele que quem pediu manda o
    /// próximo prompt, se <paramref name="canContinue"/>.
    /// </summary>
    private static JsonElement Data(Guid sessionId, HeadlessResult? result, bool canContinue, string? prompt = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["agentSessionId"] = sessionId,
            ["canContinue"] = canContinue,
            ["turns"] = result?.Turns,
            ["costUsd"] = result?.CostUsd,
            ["deniedTools"] = result?.DeniedTools,
        };

        if (prompt is not null)
        {
            data["prompt"] = prompt;
        }

        return JsonSerializer.SerializeToElement(data, RemoteJson.Options);
    }

    private static string Preview(string prompt)
    {
        var masked = SensitiveText.Mask(prompt.Trim());
        return masked.Length <= RemoteLimits.MaxPromptPreviewLength ? masked : $"{masked[..(RemoteLimits.MaxPromptPreviewLength - 1)]}…";
    }

    private static string Summary(HeadlessResult result)
    {
        var cost = result.CostUsd is { } usd ? string.Create(CultureInfo.InvariantCulture, $", US$ {usd:0.00}") : string.Empty;
        var denied = result.DeniedTools.Count > 0 ? $"; negado: {string.Join(", ", result.DeniedTools.Distinct())}" : string.Empty;
        return $"Respondeu em {result.Turns} {(result.Turns == 1 ? "turno" : "turnos")}{cost}{denied}.";
    }

    private static string Failure(HeadlessResult result) => result switch
    {
        { TerminalReason: "api_error" } when result.Text?.Contains("authenticate", StringComparison.OrdinalIgnoreCase) == true =>
            "O Claude desta máquina não está logado. Nela, rode \"claude auth login\".",
        { Subtype: "error_max_turns" } => $"Parou no teto de {result.Turns} turnos desta máquina.",
        { Subtype: "error_max_budget_usd" } => "Parou no teto de custo desta máquina.",
        _ => Clip(SensitiveText.Mask(result.Text ?? "O Claude terminou com erro.")),
    };

    private static string Tail(string text) => text.Length <= 500 ? text : text[^500..];

    private static string Clip(string text) =>
        text.Length <= RemoteLimits.MaxEventTextLength ? text : $"{text[..(RemoteLimits.MaxEventTextLength - 1)]}…";
}

/// <summary>O que do Claude sem terminal vai para a linha do tempo da conversa (E7).</summary>
public static class HeadlessTimeline
{
    /// <summary>
    /// A saída da ferramenta aparece quando é a de um comando: os testes, o
    /// build. O resultado das outras (o arquivo lido, a busca) fica na máquina.
    /// </summary>
    public static bool ShowsOutput(string tool) => tool is "Bash" or "PowerShell";
}

/// <summary><c>agent.start</c>: abre uma conversa do Claude no worktree e manda o primeiro prompt.</summary>
public sealed class AgentStartExecutor(IUseCaseRunner runner, HeadlessPromptRunner prompts) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.AgentStart;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await TargetAsync(request, cancellationToken);
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }

        return (await prompts.DetectAsync(cancellationToken)).Refusal;
    }

    public Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken) =>
        StartAsync(runner, prompts, execution, execution.Payload<ClaudeCodeSessionRequest>(), cancellationToken);

    /// <summary>Abre a conversa e manda o primeiro prompt; o "Preparar tudo" chama com o ambiente que acabou de criar.</summary>
    internal static async Task<RemoteOperationResult> StartAsync(
        IUseCaseRunner runner,
        HeadlessPromptRunner prompts,
        RemoteExecution execution,
        ClaudeCodeSessionRequest request,
        CancellationToken cancellationToken)
    {
        var (detection, refusal) = await prompts.DetectAsync(cancellationToken);

        if (refusal is not null)
        {
            return RemoteOperationResult.Failure(refusal.Code, refusal.Summary);
        }

        var target = await TargetAsync(runner, execution.Request.TaskId, request, cancellationToken);
        var sessionId = await runner.RunAsync<HeadlessSessionStore, Guid>(
            (store, token) => store.OpenAsync(target, prompts.ProviderId, detection!.ExecutablePath!, execution.Request.OperationId, token),
            CancellationToken.None);

        // A conversa acabou de nascer: ninguém mais a tem, mas um agent.stop pode chegar antes do processo.
        using var lease = prompts.TryBegin(sessionId) ?? throw new DomainException(HeadlessAgentPlanner.BusyMessage);

        return await prompts.RunAsync(execution, lease, target, detection!, request.Prompt, resume: false, cancellationToken);
    }

    private Task<HeadlessTarget> TargetAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        TargetAsync(runner, request.TaskId, RemoteOperationRules.PayloadOf<ClaudeCodeSessionRequest>(request), cancellationToken);

    private static Task<HeadlessTarget> TargetAsync(IUseCaseRunner runner, Guid? taskId, ClaudeCodeSessionRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<HeadlessAgentPlanner, HeadlessTarget>(
            (planner, token) => planner.StartAsync(taskId, request, token),
            cancellationToken);
}

/// <summary><c>agent.prompt</c>: mais um prompt na mesma conversa (<c>--resume</c>), com o perfil dela.</summary>
public sealed class AgentPromptExecutor(IUseCaseRunner runner, HeadlessPromptRunner prompts) : IRemoteOperationExecutor
{
    public const string QueuedText = "Na fila: o Claude termina o prompt anterior e responde este em seguida.";

    public const string StoppedWhileQueued = "A conversa foi parada antes deste prompt.";

    public string Kind => RemoteOperationKinds.AgentPrompt;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await TargetAsync(request, cancellationToken);
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }

        return (await prompts.DetectAsync(cancellationToken)).Refusal;
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var (detection, refusal) = await prompts.DetectAsync(cancellationToken);

        if (refusal is not null)
        {
            return RemoteOperationResult.Failure(refusal.Code, refusal.Summary);
        }

        var prompt = execution.Payload<PromptRequest>();
        var conversation = prompt.AgentSessionId ?? Guid.Empty;
        HeadlessPromptLease? lease;

        // A conversa primeiro, antes de qualquer gravação. Com outro prompt nela, este espera a vez (E6) sem tocar em nada.
        try
        {
            lease = await prompts.WaitAsync(
                conversation,
                () => execution.EmitAsync(
                    RemoteEventKind.StepStarted,
                    QueuedText,
                    "agent",
                    JsonSerializer.SerializeToElement(new { agentSessionId = conversation, canContinue = true }),
                    CancellationToken.None),
                cancellationToken);
        }
        catch (DomainException exception)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, exception.Message);
        }

        if (lease is null)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, StoppedWhileQueued);
        }

        using var _ = lease;

        var (sessionId, target) = await TargetAsync(execution.Request, leased: true, cancellationToken);
        await runner.RunAsync<HeadlessSessionStore, bool>(
            async (store, token) =>
            {
                await store.ContinueAsync(sessionId, execution.Request.OperationId, token);
                return true;
            },
            CancellationToken.None);

        return await prompts.RunAsync(execution, lease, target, detection!, prompt, resume: true, cancellationToken);
    }

    private Task<(Guid SessionId, HeadlessTarget Target)> TargetAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        TargetAsync(request, leased: false, cancellationToken);

    private Task<(Guid SessionId, HeadlessTarget Target)> TargetAsync(RemoteOperationRequest request, bool leased, CancellationToken cancellationToken) =>
        runner.RunAsync<HeadlessAgentPlanner, (Guid, HeadlessTarget)>(
            async (planner, token) =>
            {
                var payload = RemoteOperationRules.PayloadOf<PromptRequest>(request);
                var (session, target) = await planner.ContinueAsync(
                    request.TaskId,
                    payload.AgentSessionId ?? throw new DomainException("Diga em qual conversa vai o prompt."),
                    leased,
                    token);
                return (session.Id, target);
            },
            cancellationToken);
}

/// <summary><c>agent.stop</c>: encerra o processo, se houver, e a conversa não recebe mais prompts.</summary>
public sealed class AgentStopExecutor(IUseCaseRunner runner, HeadlessConversations conversations) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.AgentStop;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await SessionAsync(request, cancellationToken);
            return null;
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var sessionId = await SessionAsync(execution.Request, cancellationToken);

        // Primeiro a sessão, depois o processo: o fim do prompt chega depois e não a reabre.
        await runner.RunAsync<HeadlessSessionStore, bool>(
            async (store, token) =>
            {
                await store.StopAsync(sessionId, token);
                return true;
            },
            CancellationToken.None);

        var wasRunning = conversations.Stop(sessionId);
        conversations.Forget(sessionId);

        return RemoteOperationResult.Success(wasRunning ? "O Claude foi parado no meio da resposta." : "Conversa encerrada.");
    }

    private Task<Guid> SessionAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<HeadlessAgentPlanner, Guid>(
            async (planner, token) =>
                (await planner.SessionAsync(request.TaskId, RemoteOperationRules.PayloadOf<AgentStopRequest>(request).AgentSessionId, token)).Id,
            cancellationToken);
}
