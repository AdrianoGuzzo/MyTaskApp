using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Terminals;

/// <summary>
/// O lado do desktop no terminal do agente (ADR-098): pedir que ele abra uma
/// sessão, seguir uma pelo canal local, e o comando da janela nativa.
/// </summary>
public interface IAgentTerminalClient
{
    /// <summary>Esta máquina tem o terminal do agente (pseudoterminal e a janela instalada)?</summary>
    PseudoTerminalSupport Support { get; }

    /// <summary>Pede ao agente desta máquina que abra a sessão; sobe o agente se ele não estiver rodando.</summary>
    Task<TerminalStartResult> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Segue a sessão pelo canal local (a vista dentro do app).</summary>
    Task<LocalTerminalChannel> AttachAsync(Guid sessionId, TerminalClientKind kind, string label, TerminalSize? size, CancellationToken cancellationToken = default);

    /// <summary>O programa da janela nativa para a sessão, pronto para o <see cref="ITerminalLauncher"/>.</summary>
    TerminalLaunchOptions WindowFor(Guid sessionId, string title);
}

/// <summary>O agente, o celular, os testes: ninguém pede terminal ao agente por aqui.</summary>
public sealed class NoAgentTerminalClient : IAgentTerminalClient
{
    public static readonly NoAgentTerminalClient Instance = new();

    public PseudoTerminalSupport Support { get; } = PseudoTerminalSupport.Unsupported(
        "este processo",
        "O terminal do agente abre pelo MyTaskApp desta máquina.");

    public Task<TerminalStartResult> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TerminalStartResult(false, null, Support.Reason, null));

    public Task<LocalTerminalChannel> AttachAsync(Guid sessionId, TerminalClientKind kind, string label, TerminalSize? size, CancellationToken cancellationToken = default) =>
        throw new DomainException(Support.Reason!);

    public TerminalLaunchOptions WindowFor(Guid sessionId, string title) => throw new DomainException(Support.Reason!);
}

/// <summary>
/// As vistas que o desktop abriu para as sessões do terminal do agente
/// (ADR-098): a janela nativa de cada uma, e a vista embutida à mostra. É por
/// elas que o aviso decide se a pessoa já está olhando, e é uma delas que
/// "Abrir terminal" traz para a frente — ou abre de novo, se foi fechada.
/// </summary>
public sealed partial class AgentTerminalWindows(
    IAgentTerminalClient client,
    ITerminalLauncher launcher,
    ITerminalWindowManager windows,
    IAgentProcessTracker processes,
    ILogger<AgentTerminalWindows> logger) : IAgentTerminalWindows
{
    private readonly Lock _gate = new();

    private readonly Dictionary<Guid, (int ProcessId, DateTimeOffset StartedAt)> _native = [];

    private readonly HashSet<Guid> _embedded = [];

    public int? WindowProcessFor(Guid sessionId)
    {
        lock (_gate)
        {
            if (_native.TryGetValue(sessionId, out var window))
            {
                if (processes.IsAlive(window.ProcessId, window.StartedAt))
                {
                    return window.ProcessId;
                }

                _native.Remove(sessionId);
            }

            // A vista embutida mora na janela do próprio app.
            return _embedded.Contains(sessionId) ? Environment.ProcessId : null;
        }
    }

    /// <summary>A vista dentro do app passou a mostrar (ou deixou de mostrar) a sessão.</summary>
    public void SetEmbedded(Guid sessionId, bool showing)
    {
        lock (_gate)
        {
            if (showing)
            {
                _embedded.Add(sessionId);
            }
            else
            {
                _embedded.Remove(sessionId);
            }
        }
    }

    /// <summary>Traz a janela nativa da sessão para a frente; fechada, abre outra. A sessão não muda.</summary>
    public async Task<bool> ShowAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        int? open;

        lock (_gate)
        {
            open = _native.TryGetValue(sessionId, out var window) && processes.IsAlive(window.ProcessId, window.StartedAt)
                ? window.ProcessId
                : null;
        }

        if (open is { } processId && await windows.FocusAsync(processId))
        {
            return true;
        }

        var launched = await launcher.LaunchAsync(client.WindowFor(sessionId, title), cancellationToken);

        if (!launched.Started)
        {
            LogWindowFailed(sessionId, launched.Error);
            throw new DomainException(launched.Error ?? "A janela do terminal não abriu.");
        }

        lock (_gate)
        {
            _native[sessionId] = (launched.ProcessId, launched.ProcessStartedAt);
        }

        LogWindowOpened(sessionId, launched.ProcessId);
        return true;
    }

    /// <summary>O título da janela: o agente e a tarefa, como o card mostra.</summary>
    public static string TitleFor(string agentName, string? taskTitle) =>
        string.IsNullOrWhiteSpace(taskTitle) ? $"{agentName} · MyTaskApp" : $"{agentName} · {taskTitle}";

    [LoggerMessage(EventId = 9841, Level = LogLevel.Information, Message = "AgentTerminalWindowOpened {SessionId} {ProcessId}")]
    private partial void LogWindowOpened(Guid sessionId, int processId);

    [LoggerMessage(EventId = 9842, Level = LogLevel.Warning, Message = "AgentTerminalWindowFailed {SessionId} {Reason}")]
    private partial void LogWindowFailed(Guid sessionId, string? reason);
}

/// <summary>
/// "Abrir Claude Code", com o modo (ADR-098): o terminal direto de hoje, ou o
/// terminal do agente em janela ou dentro do app.
/// </summary>
/// <param name="Hosting">Informado, vira o padrão das próximas aberturas; <c>null</c> usa o salvo.</param>
public sealed record OpenAgentSession(StartAgentSession Start, AgentHosting? Hosting = null);

/// <summary>
/// O caminho único das telas para abrir o agente. O terminal direto é o
/// <see cref="StartAgentSessionHandler"/> daqui; o do agente é o mesmo caso de
/// uso, rodado no agente pelo canal local, e o resultado é lido do banco que
/// os dois dividem.
/// </summary>
public sealed class OpenAgentSessionHandler(
    StartAgentSessionHandler direct,
    IAgentTerminalClient agent,
    AgentTerminalWindows terminalWindows,
    IAgentSessionRepository sessions,
    ITaskItemRepository tasks,
    IAgentCliProviders providers,
    IAgentSettingsStore settings,
    IUnitOfWork unitOfWork,
    ILogger<OpenAgentSessionHandler> logger)
{
    public async Task<AgentSessionView> HandleAsync(OpenAgentSession command, CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(command.Start.ProviderId);
        var hosting = command.Hosting ?? await settings.HostingForAsync(provider, cancellationToken);

        if (command.Hosting is { } chosen && Enum.IsDefined(chosen))
        {
            await settings.SaveHostingAsync(provider.Id, chosen, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (hosting == AgentHosting.Direct)
        {
            return await direct.HandleAsync(command.Start with { Hosting = AgentHosting.Direct }, cancellationToken);
        }

        var start = command.Start;
        var result = await agent.StartAsync(
            new TerminalStartRequest(
                start.TaskId,
                start.DevelopmentId,
                hosting,
                start.Arguments,
                start.Prompt,
                start.RunDirectly,
                start.Monitor,
                start.Model,
                start.Effort,
                start.ResumeConversation),
            cancellationToken);

        if (result is not { Ok: true, SessionId: { } sessionId })
        {
            throw new DomainException(result.Error ?? $"O agente não abriu o {provider.Name}.");
        }

        var session = await sessions.FindByIdAsync(sessionId, cancellationToken)
            ?? throw new DomainException($"O agente abriu o {provider.Name}, mas a sessão não apareceu no banco.");

        logger.LogInformation("AgentTerminalOpened {TaskId} {SessionId} {Hosting}", start.TaskId, sessionId, hosting);

        var note = result.MonitoringNote;

        if (hosting == AgentHosting.AgentWindow)
        {
            var task = await tasks.FindByIdAsync(start.TaskId, cancellationToken);

            try
            {
                await terminalWindows.ShowAsync(sessionId, AgentTerminalWindows.TitleFor(provider.Name, task?.Title), cancellationToken);
            }
            catch (DomainException exception)
            {
                // A sessão já roda no agente: o card mostra, e "Abrir terminal" tenta a janela de novo.
                note = $"{exception.Message} O {provider.Name} segue rodando no agente.";
            }
        }

        return AgentSessionView.From(session, providers) with { MonitoringNote = note };
    }
}
