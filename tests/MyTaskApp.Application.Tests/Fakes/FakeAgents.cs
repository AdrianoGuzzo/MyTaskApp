using System.Runtime.InteropServices;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Sessões em memória (ADR-030).</summary>
internal sealed class FakeAgentSessionRepository : IAgentSessionRepository
{
    private readonly List<AgentSession> _sessions = [];

    public IReadOnlyList<AgentSession> Sessions => _sessions;

    public void Seed(params AgentSession[] sessions) => _sessions.AddRange(sessions);

    public Task AddAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task<AgentSession?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.FirstOrDefault(session => session.Id == id));

    public Task<AgentSession?> FindLatestForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _sessions
                .Where(session => session.TaskItemId == taskId)
                .OrderByDescending(session => session.StartedAt)
                .ThenByDescending(session => session.Id)
                .FirstOrDefault());

    public Task<IReadOnlyList<AgentSession>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentSession>>([.. _sessions.Where(session => session.IsActive)]);
}

/// <summary>Processos de mentira: vivo é o que o teste disse que está vivo.</summary>
internal sealed class FakeAgentProcessTracker : IAgentProcessTracker
{
    private readonly Dictionary<int, DateTimeOffset> _alive = [];
    private readonly Dictionary<int, Action> _watchers = [];

    public IReadOnlyCollection<int> Watched => _watchers.Keys;

    public int DisposedWatches { get; private set; }

    public void Run(int processId, DateTimeOffset startedAt) => _alive[processId] = startedAt;

    /// <summary>O processo sai: some da lista e quem vigia é avisado.</summary>
    public void Exit(int processId)
    {
        _alive.Remove(processId);

        if (_watchers.Remove(processId, out var onExited))
        {
            onExited();
        }
    }

    public bool IsAlive(int processId, DateTimeOffset startedAt) =>
        _alive.TryGetValue(processId, out var actual) && actual == startedAt;

    public IDisposable? WatchExit(int processId, DateTimeOffset startedAt, Action onExited)
    {
        if (!IsAlive(processId, startedAt))
        {
            return null;
        }

        _watchers[processId] = onExited;
        return new Watch(this, processId);
    }

    private sealed class Watch(FakeAgentProcessTracker owner, int processId) : IDisposable
    {
        public void Dispose()
        {
            owner._watchers.Remove(processId);
            owner.DisposedWatches++;
        }
    }
}

/// <summary>Abre "terminais" sem abrir nada; o processo aberto fica vivo no tracker.</summary>
internal sealed class FakeTerminalLauncher(FakeAgentProcessTracker processes, DateTimeOffset now) : ITerminalLauncher
{
    private int _nextProcessId = 15432;

    public List<TerminalLaunchOptions> Launched { get; } = [];

    public string? FailWith { get; set; }

    /// <summary>O agente sai antes de o caso de uso conferir.</summary>
    public bool ExitsImmediately { get; set; }

    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        Launched.Add(options);

        if (FailWith is not null)
        {
            return Task.FromResult(TerminalLaunchResult.Failed(FailWith));
        }

        var processId = _nextProcessId++;

        if (!ExitsImmediately)
        {
            processes.Run(processId, now);
        }

        return Task.FromResult(new TerminalLaunchResult
        {
            Started = true,
            ProcessId = processId,
            ProcessStartedAt = now,
        });
    }
}

internal sealed class FakeTerminalWindowManager : ITerminalWindowManager
{
    public List<int> Focused { get; } = [];

    public bool Finds { get; set; } = true;

    public Task<bool> FocusAsync(int processId)
    {
        Focused.Add(processId);
        return Task.FromResult(Finds);
    }
}

internal sealed class FakeAgentCliProvider : IAgentCliProvider
{
    public const string Executable = @"C:\Users\dev\.local\bin\claude.exe";

    public static readonly AgentCliInstallGuide WindowsGuide = new(
        "Instalar",
        [new AgentCliInstallStep("Rode:", "instalar")],
        new Uri("https://example.test/setup"));

    public bool Installed { get; set; } = true;

    public int Detections { get; private set; }

    public string Id => "claude-code";

    public string Name => "Claude Code";

    public string Command => "claude";

    public Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        Detections++;

        return Task.FromResult(
            Installed
                ? new CliDetectionResult { IsInstalled = true, ExecutablePath = Executable, Version = "2.1.0" }
                : CliDetectionResult.NotInstalled("Claude Code não encontrado."));
    }

    public AgentCliInstallGuide? InstallGuideFor(OSPlatform platform) => WindowsGuide;

    public TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection) =>
        new(detection.ExecutablePath!, [], context.WorkingDirectory);
}

/// <summary>Anota quem pediu para vigiar e quem avisou mudança.</summary>
internal sealed class RecordingAgentSessionWatcher : IAgentSessionWatcher
{
    public List<AgentSessionWatch> Watches { get; } = [];

    public List<Guid> Changes { get; } = [];

    public void Watch(AgentSessionWatch watch) => Watches.Add(watch);

    public void NotifyChanged(Guid taskId) => Changes.Add(taskId);
}
