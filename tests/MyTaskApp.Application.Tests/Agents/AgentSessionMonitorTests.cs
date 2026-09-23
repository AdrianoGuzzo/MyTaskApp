using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>
/// O monitor das sessões (ADR-030): reencontra o que ficou aberto, percebe o fim
/// do processo por evento e, ao ser descartado, não encerra ninguém.
/// </summary>
public class AgentSessionMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly InlineUseCaseRunner _runner = new();
    private readonly AgentSessionMonitor _monitor;
    private readonly List<Guid> _changed = [];
    private readonly TaskItem _task = TaskItem.Create("Implementar autenticação", Now);

    public AgentSessionMonitorTests()
    {
        _monitor = new AgentSessionMonitor(
            _runner,
            _processes,
            _time,
            Options.Create(new ApplicationOptions { AgentSessionReconcileSeconds = 60 }),
            NullLogger<AgentSessionMonitor>.Instance);

        _runner.Register(() => new ReconcileAgentSessionsHandler(
            _sessions, _tasks, _processes, _monitor, _time, NullLogger<ReconcileAgentSessionsHandler>.Instance));
        _runner.Register(() => new EndAgentSessionHandler(
            _sessions, _tasks, _monitor, _time, NullLogger<EndAgentSessionHandler>.Instance));

        _monitor.SessionsChanged += _changed.Add;
    }

    private AgentSession Running(int processId, bool alive)
    {
        var session = AgentSession.Create(_task.Id, "claude-code", @"C:\claude.exe", @"C:\wt", Now);
        session.MarkRunning(processId, Now);
        _sessions.Seed(session);

        if (alive)
        {
            _processes.Run(processId, Now);
        }

        return session;
    }

    [Fact]
    public async Task Starting_RecoversTheSessionsLeftOpen_Immediately()
    {
        var alive = Running(15432, alive: true);
        var gone = Running(15001, alive: false);

        await using (_monitor)
        {
            _monitor.Start();
            await _runner.Idle();

            alive.Status.Should().Be(AgentSessionStatus.Running);
            gone.Status.Should().Be(AgentSessionStatus.Exited);
            _monitor.WatchCount.Should().Be(1);
            _changed.Should().Equal(_task.Id);
        }
    }

    /// <summary>O fim chega pelo evento do processo, sem esperar o timer.</summary>
    [Fact]
    public async Task TheProcessExiting_EndsTheSession_WithoutWaitingForTheTimer()
    {
        var session = Running(15432, alive: true);

        await using (_monitor)
        {
            await _monitor.TickAsync(Ct);

            _processes.Exit(15432);
            await _runner.Idle();

            session.Status.Should().Be(AgentSessionStatus.Exited);
            _monitor.WatchCount.Should().Be(0);
            _changed.Should().Equal(_task.Id);
        }
    }

    [Fact]
    public async Task WatchingTheSameSessionTwice_KeepsOneWatch()
    {
        var session = Running(15432, alive: true);
        var watch = AgentSessionWatch.For(session)!;

        await using (_monitor)
        {
            _monitor.Watch(watch);
            _monitor.Watch(watch);

            _monitor.WatchCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task WatchingAProcessThatIsAlreadyGone_EndsTheSession()
    {
        var session = Running(15432, alive: false);

        await using (_monitor)
        {
            _monitor.Watch(AgentSessionWatch.For(session)!);
            await _runner.Idle();

            session.Status.Should().Be(AgentSessionStatus.Exited);
        }
    }

    [Fact]
    public async Task TheSafetyNet_RunsAtTheConfiguredPace_NotInATightLoop()
    {
        await using (_monitor)
        {
            _monitor.Start();
            _time.Advance(TimeSpan.FromSeconds(59));
            await _runner.Idle();

            _runner.Invocations.Should().Be(1);

            _time.Advance(TimeSpan.FromSeconds(1));
            await _runner.Idle();

            _runner.Invocations.Should().Be(2);
        }
    }

    /// <summary>Fechar o app só para de vigiar: o Claude continua no terminal.</summary>
    [Fact]
    public async Task Disposing_StopsWatching_WithoutEndingTheSession()
    {
        var session = Running(15432, alive: true);
        await _monitor.TickAsync(Ct);

        await _monitor.DisposeAsync();

        _processes.DisposedWatches.Should().Be(1);
        _processes.IsAlive(15432, Now).Should().BeTrue();
        session.Status.Should().Be(AgentSessionStatus.Running);
    }

    [Fact]
    public void ReconcilePeriod_IsClampedAwayFromATightLoop()
    {
        new ApplicationOptions { AgentSessionReconcileSeconds = 0 }.ToAgentSessionReconcilePeriod()
            .Should().Be(TimeSpan.FromSeconds(10));
    }

    /// <summary>Roda o handler registrado de verdade, sem contêiner.</summary>
    private sealed class InlineUseCaseRunner : IUseCaseRunner
    {
        private readonly Dictionary<Type, Func<object>> _factories = [];
        private readonly List<Task> _running = [];

        public int Invocations { get; private set; }

        public void Register<THandler>(Func<THandler> factory)
            where THandler : notnull =>
            _factories[typeof(THandler)] = () => factory();

        public async Task Idle()
        {
            while (true)
            {
                Task[] pending;

                lock (_running)
                {
                    pending = [.. _running];
                    _running.Clear();
                }

                if (pending.Length == 0)
                {
                    return;
                }

                await Task.WhenAll(pending);
            }
        }

        public Task<TResult> RunAsync<THandler, TResult>(
            Func<THandler, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
            where THandler : notnull
        {
            Invocations++;
            var task = operation((THandler)_factories[typeof(THandler)](), cancellationToken);

            lock (_running)
            {
                _running.Add(task);
            }

            return task;
        }

        public Task RunAsync<THandler>(
            Func<THandler, CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
            where THandler : notnull =>
            RunAsync<THandler, bool>(
                async (handler, token) =>
                {
                    await operation(handler, token);
                    return true;
                },
                cancellationToken);
    }
}
