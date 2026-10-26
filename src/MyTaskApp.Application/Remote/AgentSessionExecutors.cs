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
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
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

    public async Task<(AgentSession Session, HeadlessTarget Target)> ContinueAsync(Guid? taskId, Guid agentSessionId, CancellationToken cancellationToken = default)
    {
        var session = await SessionAsync(taskId, agentSessionId, cancellationToken);

        if (!session.CanContinue)
        {
            throw new DomainException(session.Status switch
            {
                AgentSessionStatus.Stopped => "A conversa foi parada. Abra outra.",
                AgentSessionStatus.Starting or AgentSessionStatus.Running => "O Claude ainda está respondendo o prompt anterior.",
                _ => "A conversa não chegou a abrir. Abra outra.",
            });
        }

        var profile = AgentToolProfiles.Find(session.HeadlessProfileId)
            ?? throw new DomainException("O perfil desta conversa não existe mais nesta máquina. Abra outra.");
        var (task, development) = await DevelopmentAsync(taskId, session.TaskDevelopmentId ?? Guid.Empty, cancellationToken);

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
public sealed class HeadlessSessionStore(IAgentSessionRepository sessions, IUnitOfWork unitOfWork, TimeProvider timeProvider)
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

    public Task ContinueAsync(Guid sessionId, Guid operationId, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, session => session.ContinueWith(operationId, timeProvider.GetUtcNow()), cancellationToken);

    public Task RunningAsync(Guid sessionId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, session => session.MarkRunning(processId, processStartedAt), cancellationToken);

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

                session.RecordActivity(activity, message, now);
                session.MarkExited(now);
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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _remembered = new();

    public IDisposable Track(Guid sessionId, CancellationTokenSource prompt)
    {
        _running[sessionId] = prompt;
        return new Release(() => _running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(sessionId, prompt)));
    }

    /// <summary>Encerra o prompt da conversa, se houver um rodando aqui.</summary>
    public bool Stop(Guid sessionId)
    {
        if (!_running.TryGetValue(sessionId, out var prompt))
        {
            return false;
        }

        try
        {
            prompt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public bool IsRemembered(Guid sessionId, string tool) =>
        _remembered.TryGetValue(sessionId, out var tools) && tools.ContainsKey(tool);

    public void Remember(Guid sessionId, string tool) =>
        _remembered.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal))[tool] = true;

    public void Forget(Guid sessionId) => _remembered.TryRemove(sessionId, out _);

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
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

    public async Task<RemoteOperationResult> RunAsync(
        RemoteExecution execution,
        Guid sessionId,
        HeadlessTarget target,
        CliDetectionResult detection,
        PromptRequest prompt,
        bool resume,
        CancellationToken cancellationToken)
    {
        var conversation = await runner.RunAsync<IAgentSessionRepository, Guid>(
            async (sessions, token) => Guid.Parse((await sessions.FindByIdAsync(sessionId, token))!.ExternalSessionId!),
            cancellationToken);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var tracked = conversations.Track(sessionId, stop);
        var activity = AgentActivity.Working;
        HeadlessResult? result = null;

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
                    var summary = SensitiveText.Mask(provider.Summarize(request.ToolName, request.Input));
                    await SetActivityAsync(AgentActivity.WaitingForUser, $"Aprovar {request.ToolName}?");
                    var decision = await DecideAsync(execution, sessionId, request.ToolName, summary, request.ToolUseId, token);
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
        await execution.EmitAsync(
            RemoteEventKind.StepStarted,
            $"Claude em {target.Branch} · perfil {target.Profile.Name}.",
            "agent",
            Data(sessionId, null, canContinue: true),
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
                                await execution.EmitAsync(
                                    RemoteEventKind.ToolUse,
                                    Clip(SensitiveText.Mask($"{use.Name}: {use.Summary}")),
                                    "agent",
                                    JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["tool"] = use.Name }, RemoteJson.Options),
                                    CancellationToken.None);
                                break;

                            case HeadlessToolResult { IsError: true } failed:
                                await execution.EmitAsync(RemoteEventKind.Warning, Clip(SensitiveText.Mask(failed.Summary)), "agent", cancellationToken: CancellationToken.None);
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

    /// <summary>A ferramenta fora do perfil: lembrada nesta conversa, ou perguntada a quem pediu.</summary>
    private async Task<ToolPermissionDecision> DecideAsync(
        RemoteExecution execution,
        Guid sessionId,
        string tool,
        string summary,
        string? toolUseId,
        CancellationToken cancellationToken)
    {
        if (conversations.IsRemembered(sessionId, tool))
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
                    },
                    RemoteJson.Options)),
            cancellationToken);

        if (approval.IsAllowed && approval.RememberForThisSession)
        {
            conversations.Remember(sessionId, tool);
        }

        return approval.IsAllowed
            ? ToolPermissionDecision.Allow()
            : ToolPermissionDecision.Deny(approval.Outcome == RemoteApprovalOutcome.Expired
                ? "Ninguém respondeu a tempo: negado. Siga sem essa ferramenta."
                : $"Negado por {approval.ControllerNickname ?? execution.ControllerNickname}. Siga sem essa ferramenta.");
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
    private static JsonElement Data(Guid sessionId, HeadlessResult? result, bool canContinue) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["agentSessionId"] = sessionId,
                ["canContinue"] = canContinue,
                ["turns"] = result?.Turns,
                ["costUsd"] = result?.CostUsd,
                ["deniedTools"] = result?.DeniedTools,
            },
            RemoteJson.Options);

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

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var (detection, refusal) = await prompts.DetectAsync(cancellationToken);

        if (refusal is not null)
        {
            return RemoteOperationResult.Failure(refusal.Code, refusal.Summary);
        }

        var target = await TargetAsync(execution.Request, cancellationToken);
        var payload = execution.Payload<ClaudeCodeSessionRequest>();
        var sessionId = await runner.RunAsync<HeadlessSessionStore, Guid>(
            (store, token) => store.OpenAsync(target, prompts.ProviderId, detection!.ExecutablePath!, execution.Request.OperationId, token),
            CancellationToken.None);

        return await prompts.RunAsync(execution, sessionId, target, detection!, payload.Prompt, resume: false, cancellationToken);
    }

    private Task<HeadlessTarget> TargetAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<HeadlessAgentPlanner, HeadlessTarget>(
            (planner, token) => planner.StartAsync(request.TaskId, RemoteOperationRules.PayloadOf<ClaudeCodeSessionRequest>(request), token),
            cancellationToken);
}

/// <summary><c>agent.prompt</c>: mais um prompt na mesma conversa (<c>--resume</c>), com o perfil dela.</summary>
public sealed class AgentPromptExecutor(IUseCaseRunner runner, HeadlessPromptRunner prompts) : IRemoteOperationExecutor
{
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

        var (sessionId, target) = await TargetAsync(execution.Request, cancellationToken);
        await runner.RunAsync<HeadlessSessionStore, bool>(
            async (store, token) =>
            {
                await store.ContinueAsync(sessionId, execution.Request.OperationId, token);
                return true;
            },
            CancellationToken.None);

        return await prompts.RunAsync(execution, sessionId, target, detection!, execution.Payload<PromptRequest>(), resume: true, cancellationToken);
    }

    private Task<(Guid SessionId, HeadlessTarget Target)> TargetAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<HeadlessAgentPlanner, (Guid, HeadlessTarget)>(
            async (planner, token) =>
            {
                var payload = RemoteOperationRules.PayloadOf<PromptRequest>(request);
                var (session, target) = await planner.ContinueAsync(
                    request.TaskId,
                    payload.AgentSessionId ?? throw new DomainException("Diga em qual conversa vai o prompt."),
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
