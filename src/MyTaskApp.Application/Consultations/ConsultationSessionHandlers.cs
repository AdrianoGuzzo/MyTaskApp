using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>Abre uma Consulta Inteligente nova, com a pergunta como primeira mensagem.</summary>
/// <param name="Instructions">
/// "Editar só para esta sessão": o texto que substitui as instruções geradas
/// agora; não é gravado. <c>null</c> = as geradas.
/// </param>
public sealed record StartTaskConsultation(Guid TaskId, string Question, string? Instructions = null);

public sealed class StartTaskConsultationHandler(ConsultationResolver resolver, ConsultationLauncher launcher)
{
    public async Task<AgentSessionView> HandleAsync(StartTaskConsultation command, CancellationToken cancellationToken = default)
    {
        var question = command.Question?.Trim();

        if (string.IsNullOrEmpty(question))
        {
            throw new DomainException("Escreva a pergunta.");
        }

        var state = await resolver.ResolveAsync(command.TaskId, cancellationToken);

        return await launcher.LaunchAsync(
            state, Guid.NewGuid().ToString(), resume: false, question, command.Instructions, cancellationToken);
    }
}

/// <summary>Volta a uma consulta encerrada, na mesma conversa do agente.</summary>
public sealed record ResumeTaskConsultation(Guid TaskId, Guid SessionId);

/// <remarks>
/// Uma sessão nova do MyTaskApp, com segredo novo e as instruções de agora,
/// sobre a conversa antiga (<c>--resume</c>): o histórico volta, as travas são
/// as de hoje.
/// </remarks>
public sealed class ResumeTaskConsultationHandler(
    ConsultationResolver resolver,
    ConsultationLauncher launcher,
    IAgentSessionRepository sessions)
{
    public async Task<AgentSessionView> HandleAsync(ResumeTaskConsultation command, CancellationToken cancellationToken = default)
    {
        var previous = await sessions.FindByIdAsync(command.SessionId, cancellationToken);

        if (previous is not { IsConsultation: true } || previous.TaskItemId != command.TaskId)
        {
            throw new DomainException("Consulta não encontrada nesta tarefa.");
        }

        if (previous.IsActive)
        {
            throw new DomainException("Esta consulta ainda está aberta. Use \"Ir para o terminal\".");
        }

        if (previous.Status == AgentSessionStatus.Failed || previous.ExternalSessionId is not { } conversation)
        {
            throw new DomainException("Esta consulta não chegou a abrir, e não há conversa para retomar.");
        }

        var state = await resolver.ResolveAsync(command.TaskId, cancellationToken);

        return await launcher.LaunchAsync(state, conversation, resume: true, question: null, instructions: null, cancellationToken);
    }
}

/// <summary>
/// O fluxo de abrir uma consulta (ADR-081), o mesmo da ADR-030 com as travas
/// da consulta: configuração sem problemas → nenhuma consulta aberta na tarefa
/// → agente instalado e capaz de abrir só leitura → banco conferido como só
/// leitura → sessão gravada como "iniciando" → terminal aberto → PID gravado.
/// </summary>
/// <remarks>
/// <para>
/// O segredo das ferramentas de banco nasce aqui, vai no ambiente do processo
/// e só o hash fica no banco. Ele vale enquanto a sessão está ativa.
/// </para>
/// <para>
/// Falha ao abrir o terminal não lança: vira uma sessão
/// <see cref="AgentSessionStatus.Failed"/> com o motivo, como no
/// desenvolvimento.
/// </para>
/// </remarks>
public sealed class ConsultationLauncher(
    ITaskConsultationRepository consultations,
    IAgentSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IAgentCliProviders providers,
    ITerminalLauncher terminal,
    IAgentProcessTracker processes,
    IAgentSessionWatcher watcher,
    IAgentSettingsStore settings,
    IAgentEventEndpoint events,
    IConsultationToolsEndpoint tools,
    ConsultationReadAccess readAccess,
    TimeProvider timeProvider,
    ILogger<ConsultationLauncher> logger)
{
    /// <summary>Instruções editadas à mão maiores que isso não são instruções.</summary>
    public const int MaxInstructionsLength = 20_000;

    public async Task<AgentSessionView> LaunchAsync(
        ConsultationState state,
        string conversationId,
        bool resume,
        string? question,
        string? instructions,
        CancellationToken cancellationToken)
    {
        if (state.Problems.Count > 0)
        {
            throw new DomainException(string.Join(" ", state.Problems));
        }

        var task = state.Task;
        await EnsureNoActiveConsultationAsync(task.Id, cancellationToken);

        var provider = providers.Default;
        var builder = provider as IConsultationLaunchBuilder
            ?? throw new DomainException($"O {provider.Name} não sabe abrir uma consulta só de leitura.");

        var detection = await provider.DetectAsync(cancellationToken);

        if (!detection.IsInstalled || detection.ExecutablePath is null)
        {
            throw new DomainException($"{provider.Name} não encontrado.");
        }

        var model = Option(provider.Models, state.Consultation?.Model, "Modelo");
        var effort = Option(provider.Efforts, state.Consultation?.Effort, "Nível de esforço");
        var (toolsAccess, serverVersion) = await DatabaseAsync(state, cancellationToken);

        var text = string.IsNullOrWhiteSpace(instructions)
            ? ConsultationContextBuilder.Build(ContextOf(state, serverVersion))
            : instructions.Trim();

        if (text.Length > MaxInstructionsLength)
        {
            throw new DomainException($"As instruções da sessão podem ter até {MaxInstructionsLength} caracteres.");
        }

        var directories = state.ReadySources.Select(source => source.Path!).ToList();
        await RememberAsync(state, question, cancellationToken);

        var session = AgentSession.CreateConsultation(
            task.Id, provider.Id, detection.ExecutablePath, directories[0], conversationId, timeProvider.GetUtcNow());

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (toolsAccess is not null)
        {
            var (token, hash) = AgentHookToken.Create();
            session.GrantConsultationAccess(hash);
            environment[toolsAccess.TokenVariable] = token;
        }

        var monitoring = await MonitoringAsync(provider, session, directories[0], cancellationToken);

        var launch = builder.CreateConsultationLaunch(
            new ConsultationLaunchRequest(
                session.Id,
                conversationId,
                resume,
                $"Consulta · {task.Title}",
                directories,
                text,
                question,
                toolsAccess,
                [.. model?.Arguments ?? [], .. effort?.Arguments ?? []],
                environment,
                monitoring),
            detection);

        await sessions.AddAsync(session, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        TerminalLaunchResult result;

        try
        {
            result = await terminal.LaunchAsync(launch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "ConsultationTerminalLaunchThrew {TaskId}", task.Id);
            result = TerminalLaunchResult.Failed("Não foi possível abrir o terminal.");
        }

        if (!result.Started)
        {
            session.MarkFailed(result.Error ?? "Não foi possível abrir o terminal.", timeProvider.GetUtcNow());
            await SaveAndNotifyAsync(session, cancellationToken);

            logger.LogWarning("ConsultationFailedToStart {TaskId} {SessionId} {Reason}", task.Id, session.Id, session.FailureReason);

            return AgentSessionView.From(session, providers);
        }

        session.MarkRunning(result.ProcessId, result.ProcessStartedAt);

        if (!processes.IsAlive(result.ProcessId, result.ProcessStartedAt))
        {
            session.MarkExited(timeProvider.GetUtcNow());
        }

        await SaveAndNotifyAsync(session, cancellationToken);

        if (AgentSessionWatch.For(session) is { } watch)
        {
            watcher.Watch(watch);
        }

        // Só ids, contagens e escolhas: a pergunta e as instruções podem ter dado do usuário.
        logger.LogInformation(
            "ConsultationStarted {TaskId} {SessionId} {Resume} {Directories} {WithDatabase} {Model} {Effort} {Monitored}",
            task.Id,
            session.Id,
            resume,
            directories.Count,
            toolsAccess is not null,
            model?.Value,
            effort?.Value,
            session.IsMonitored);

        return AgentSessionView.From(session, providers);
    }

    /// <summary>O contexto da consulta como está agora; o banco entra sem versão, e sem as ferramentas se o MCP está fora.</summary>
    public static ConsultationContext ContextOf(ConsultationState state, string? serverVersion) =>
        new(
            state.Task.Title,
            state.TagName,
            state.ReadySources
                .Select(source => new ConsultationContextSource(
                    source.Name,
                    source.Alias,
                    source.Path!,
                    source.RepositoryUrl,
                    source.Branch,
                    source.Kind == ConsultationSourceKind.Worktree))
                .ToList(),
            state.Connection is { Database: { } name } connection && state.Database is { } selection
                ? new ConsultationContextDatabase(
                    selection.Label, name, serverVersion, connection.Environment, ConsultationLimits.Default, ConsultationToolNames.All)
                : null,
            state.Consultation?.Instructions);

    /// <summary>
    /// Com banco: o servidor MCP tem de estar no ar, e o usuário que vai ler,
    /// conferido no catálogo, só lê. Qualquer "não" impede de abrir — abrir
    /// sem o banco seria responder sem os dados que a pessoa escolheu.
    /// </summary>
    private async Task<(ConsultationToolsAccess? Tools, string? Version)> DatabaseAsync(
        ConsultationState state,
        CancellationToken cancellationToken)
    {
        if (state.Connection is not { } chosen)
        {
            return (null, null);
        }

        var endpoint = tools.Address
            ?? throw new DomainException(
                "Para consultar o banco, o servidor MCP do MyTaskApp precisa estar ligado. Ligue-o, ou escolha \"Somente código-fonte\".");

        var reader = await readAccess.ReaderForAsync(chosen, cancellationToken);
        ReadAccessCheck check;

        try
        {
            check = await readAccess.CheckAsync(reader, cancellationToken);
        }
        catch (DomainException exception)
        {
            throw new DomainException($"Não foi possível conferir o acesso ao banco: {exception.Message}");
        }

        if (!check.IsReadOnly)
        {
            throw new DomainException(
                $"O usuário {check.User} pode alterar dados ({string.Join("; ", check.Problems)}). "
                + "Use \"Preparar acesso de leitura\" antes de consultar este banco.");
        }

        return (new ConsultationToolsAccess(endpoint, ConsultationToolNames.TokenVariable, ConsultationToolNames.All), check.ServerVersion);
    }

    /// <summary>
    /// Uma consulta aberta por tarefa: abrir outra esconderia o terminal da
    /// primeira. A que já morreu é encerrada aqui — o índice único do banco
    /// recusaria as duas ativas.
    /// </summary>
    private async Task EnsureNoActiveConsultationAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var latest = await sessions.ListConsultationsForTaskAsync(taskId, ConsultationViewFactory.HistoryLimit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var ended = false;

        foreach (var session in latest.Where(session => session.IsActive))
        {
            if (!AgentSessionReconciler.EndIfGone(session, processes, now))
            {
                throw new DomainException("Esta tarefa já tem uma Consulta Inteligente aberta. Use \"Ir para o terminal\" para ir até ela.");
            }

            ended = true;
        }

        if (ended)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A pergunta fica guardada (reaparece na aba); sem configuração salva, as
    /// sugestões da aba viram a configuração — é com elas que a consulta abre.
    /// </summary>
    private async Task RememberAsync(ConsultationState state, string? question, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var consultation = state.Consultation;

        if (consultation is null)
        {
            consultation = TaskConsultation.Create(state.Task.Id, now);
            consultation.SetSubject(state.TagId, now);
            consultation.SetSources(state.Sources.Where(source => source.IsSelected).Select(source => source.Reference), now);
            await consultations.AddAsync(consultation, cancellationToken);
        }

        if (question is not null)
        {
            consultation.RememberQuestion(question, now);
        }
    }

    /// <summary>Como no desenvolvimento (ADR-037): acompanhar é um extra e nunca impede de abrir.</summary>
    private async Task<AgentMonitoring?> MonitoringAsync(
        IAgentCliProvider provider,
        AgentSession session,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!await settings.MonitoringForAsync(provider, cancellationToken)
            || events.Address is not { } endpoint
            || provider.MonitoringUnavailableReason(workingDirectory, endpoint) is not null)
        {
            return null;
        }

        var (token, hash) = AgentHookToken.Create();
        session.EnableMonitoring(hash);

        return new AgentMonitoring(endpoint, AgentMonitoringEnvironment.For(session, workingDirectory, string.Empty, token, endpoint));
    }

    private static AgentCliOption? Option(IReadOnlyList<AgentCliOption> options, string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null
        : AgentCliOption.Find(options, value) ?? throw new DomainException($"{name} desconhecido: {value}.");

    private async Task SaveAndNotifyAsync(AgentSession session, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        watcher.NotifyChanged(session.TaskItemId);
    }
}
