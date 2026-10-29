using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Terminals;

/// <summary>Onde o "Abrir Claude Code" roda o agente (ADR-098). Gravado nas preferências; não reordenar.</summary>
public enum AgentHosting
{
    /// <summary>Num terminal do sistema aberto pelo app, como sempre foi (ADR-030).</summary>
    Direct = 0,

    /// <summary>No terminal do agente, mostrado numa janela de terminal nativa.</summary>
    AgentWindow = 1,

    /// <summary>No terminal do agente, mostrado dentro do MyTaskApp.</summary>
    AgentEmbedded = 2,
}

/// <summary>
/// As sessões de terminal hospedadas (ADR-098). Só o MyTaskApp Agent hospeda
/// (<see cref="IsHost"/>); no desktop, a porta existe para os casos de uso
/// montarem iguais, e recusa.
/// </summary>
public interface ITerminalSessions
{
    /// <summary>Este processo é quem hospeda os terminais do agente.</summary>
    bool IsHost { get; }

    PseudoTerminalSupport Support { get; }

    /// <summary>Uma sessão hospedada mudou, entrou ou saiu.</summary>
    event EventHandler? Changed;

    /// <summary>A sessão ainda hospedada (viva, ou saída há pouco); <c>null</c> se não há.</summary>
    TerminalSessionHost? Find(Guid sessionId);

    IReadOnlyList<TerminalSessionHost> List();

    /// <summary>
    /// Abre o programa num pseudoterminal e passa a hospedá-lo. Como o
    /// <see cref="ITerminalLauncher"/>: falha ao abrir volta no resultado.
    /// </summary>
    Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default);

    /// <summary>Um aviso dos hooks, já conferido, para a sessão a que pertence.</summary>
    void Observe(AgentEvent agentEvent);

    /// <summary>
    /// A atividade de uma sessão hospedada mudou no banco, ou ela terminou.
    /// O agente não tem tela: quem tem decide se chama a pessoa.
    /// </summary>
    void NotifyActivity(Guid sessionId);

    /// <summary>Ver <see cref="NotifyActivity"/>.</summary>
    event EventHandler<Guid>? ActivityChanged;
}

/// <summary>O desktop, os testes: não hospedam terminal.</summary>
public sealed class NoTerminalSessions : ITerminalSessions
{
    public static readonly NoTerminalSessions Instance = new();

    public bool IsHost => false;

    public PseudoTerminalSupport Support { get; } = PseudoTerminalSupport.Unsupported(
        "este processo",
        "O terminal do agente roda no MyTaskApp Agent desta máquina.");

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public TerminalSessionHost? Find(Guid sessionId) => null;

    public IReadOnlyList<TerminalSessionHost> List() => [];

    public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default) =>
        Task.FromResult(TerminalLaunchResult.Failed(Support.Reason!));

    public void Observe(AgentEvent agentEvent)
    {
    }

    public void NotifyActivity(Guid sessionId)
    {
    }

    public event EventHandler<Guid>? ActivityChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// As sessões que o agente hospeda (ADR-098). Cada uma vive enquanto o
/// processo vive; quando ele sai, a sessão é encerrada no banco e a tela fica
/// mais <see cref="ExitedRetention"/> para quem quiser ver como terminou.
/// </summary>
/// <remarks>
/// Até <see cref="MaxSessions"/> abertas ao mesmo tempo: cada uma segura um
/// processo, uma tela e as filas dos clientes, e nenhum limite natural
/// existiria se não este.
/// </remarks>
public sealed partial class TerminalSessionRegistry(
    IPseudoTerminalFactory factory,
    IUseCaseRunner runner,
    TimeProvider time,
    ILoggerFactory loggers) : ITerminalSessions, IAsyncDisposable
{
    public const int MaxSessions = 8;

    /// <summary>Depois de sair, a tela final continua visível por este tempo.</summary>
    public static readonly TimeSpan ExitedRetention = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger = loggers.CreateLogger<TerminalSessionRegistry>();

    private readonly Lock _gate = new();

    private readonly Dictionary<Guid, TerminalSessionHost> _hosts = [];

    private bool _disposed;

    public event EventHandler? Changed;

    public event EventHandler<Guid>? ActivityChanged;

    public bool IsHost => true;

    public PseudoTerminalSupport Support => factory.Support;

    public TerminalSessionHost? Find(Guid sessionId)
    {
        lock (_gate)
        {
            return _hosts.GetValueOrDefault(sessionId);
        }
    }

    public IReadOnlyList<TerminalSessionHost> List()
    {
        lock (_gate)
        {
            return [.. _hosts.Values];
        }
    }

    public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(info);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_hosts.Values.Count(host => host.IsRunning) >= MaxSessions)
            {
                return Task.FromResult(TerminalLaunchResult.Failed(
                    $"Este agente já tem {MaxSessions} terminais abertos. Feche um antes de abrir outro."));
            }
        }

        IPseudoTerminal terminal;

        try
        {
            terminal = factory.Start(new PseudoTerminalStart(
                options.Executable,
                options.Arguments,
                options.WorkingDirectory,
                options.Environment,
                TerminalSize.Default,
                options.RemovedVariables));
        }
        catch (DomainException exception)
        {
            LogStartFailed(info.SessionId, exception.Message);
            return Task.FromResult(TerminalLaunchResult.Failed(exception.Message));
        }

        var host = new TerminalSessionHost(info, terminal, time, loggers.CreateLogger<TerminalSessionHost>());
        host.Changed += OnHostChanged;

        lock (_gate)
        {
            _hosts[info.SessionId] = host;
        }

        _ = FollowAsync(host);
        LogHosted(info.SessionId, terminal.ProcessId);
        RaiseChanged();

        return Task.FromResult(new TerminalLaunchResult
        {
            Started = true,
            ProcessId = terminal.ProcessId,
            ProcessStartedAt = terminal.ProcessStartedAt,
        });
    }

    public void Observe(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        Find(agentEvent.AgentSessionId)?.Observe(agentEvent);
    }

    public void NotifyActivity(Guid sessionId) => ActivityChanged?.Invoke(this, sessionId);

    /// <summary>Desligando o agente: cada Claude recebe o fim do terminal, como numa janela fechada.</summary>
    public async ValueTask DisposeAsync()
    {
        List<TerminalSessionHost> hosts;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
        }

        await Task.WhenAll(hosts.Select(host => host.DisposeAsync().AsTask()));
    }

    /// <summary>O processo saiu: a sessão encerra no banco, e a tela fica um tempo.</summary>
    private async Task FollowAsync(TerminalSessionHost host)
    {
        try
        {
            await host.Completion.ConfigureAwait(false);
            await runner.RunAsync<EndAgentSessionHandler>(
                (handler, token) => handler.HandleAsync(new EndAgentSession(host.SessionId), token),
                CancellationToken.None).ConfigureAwait(false);

            // O aviso de "esperando você" sai da tela de quem o mostrava.
            NotifyActivity(host.SessionId);

            await Task.Delay(ExitedRetention, time).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFollowFailed(exception, host.SessionId);
        }

        bool removed;

        lock (_gate)
        {
            removed = _hosts.TryGetValue(host.SessionId, out var current) && current == host && _hosts.Remove(host.SessionId);
        }

        if (removed)
        {
            host.Changed -= OnHostChanged;
            await host.DisposeAsync().ConfigureAwait(false);
            RaiseChanged();
        }
    }

    private void OnHostChanged(object? sender, EventArgs e) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    [LoggerMessage(EventId = 9811, Level = LogLevel.Information, Message = "TerminalSessionHosted {SessionId} {ProcessId}")]
    private partial void LogHosted(Guid sessionId, int processId);

    [LoggerMessage(EventId = 9812, Level = LogLevel.Warning, Message = "TerminalSessionStartFailed {SessionId} {Reason}")]
    private partial void LogStartFailed(Guid sessionId, string reason);

    [LoggerMessage(EventId = 9813, Level = LogLevel.Error, Message = "TerminalSessionFollowFailed {SessionId}")]
    private partial void LogFollowFailed(Exception exception, Guid sessionId);
}
