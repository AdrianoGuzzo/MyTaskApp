using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Tests.QuickCommands;

/// <summary>
/// O monitor dos comandos rápidos (ADR-051): reencontra os terminais abertos,
/// percebe o fim pelo evento do processo e não toma por interrompido o que roda
/// neste processo. Descartá-lo não encerra ninguém.
/// </summary>
public class CommandExecutionMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeCommandExecutionRepository _executions = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly InlineUseCaseRunner _runner = new();
    private readonly CommandExecutionMonitor _monitor;
    private readonly List<Guid> _changed = [];
    private readonly Guid _taskId = Guid.CreateVersion7();

    public CommandExecutionMonitorTests()
    {
        _monitor = new CommandExecutionMonitor(_runner, _processes, NullLogger<CommandExecutionMonitor>.Instance);

        _runner.Register(() => new ReconcileCommandExecutionsHandler(_executions, _executions, _processes, _monitor, _time));
        _runner.Register(() => new EndCommandExecutionHandler(
            _executions, _executions, _monitor, _time, NullLogger<EndCommandExecutionHandler>.Instance));

        _monitor.ExecutionsChanged += _changed.Add;
    }

    private CommandExecution Execution(CommandMode mode, bool keepOpen = true)
    {
        var execution = CommandExecution.Create(
            _taskId, Guid.CreateVersion7(), Guid.CreateVersion7(), null, "Executar", "dotnet run", @"C:\wt", mode, keepOpen, Now);
        _executions.Seed(execution);
        return execution;
    }

    private CommandExecution Terminal(int processId, bool alive, bool keepOpen = true)
    {
        var execution = Execution(CommandMode.Terminal, keepOpen);
        execution.MarkRunning(processId, Now);

        if (alive)
        {
            _processes.Run(processId, Now);
        }

        return execution;
    }

    [Fact]
    public async Task Starting_FindsTheTerminalsLeftOpen_AndEndsTheClosedOnes()
    {
        var open = Terminal(24680, alive: true);
        var closed = Terminal(24681, alive: false);

        using (_monitor)
        {
            _monitor.Start();
            await _runner.Idle();

            open.Status.Should().Be(CommandExecutionStatus.Running);
            closed.Status.Should().Be(CommandExecutionStatus.Completed);
            closed.ExitCode.Should().BeNull();
            _monitor.WatchCount.Should().Be(1);
            _changed.Should().Equal(_taskId);
        }
    }

    [Fact]
    public async Task Starting_MarksHiddenRunsLeftRunning_AsInterrupted()
    {
        var orphan = Execution(CommandMode.Execute);
        orphan.MarkRunning();

        var result = await _monitor.ReconcileAsync(Ct);

        result.Ended.Should().Be(1);
        orphan.Status.Should().Be(CommandExecutionStatus.Failed);
        orphan.FailureReason.Should().Contain("fechado");
    }

    [Fact]
    public async Task AHiddenRunInThisProcess_IsNotTakenAsInterrupted()
    {
        var running = Execution(CommandMode.Execute);
        running.MarkRunning();

        using var cancellation = new CancellationTokenSource();
        using (_monitor.TrackInProcess(running.Id, cancellation))
        {
            await _monitor.ReconcileAsync(Ct);

            running.Status.Should().Be(CommandExecutionStatus.Running);
            _monitor.IsInProcess(running.Id).Should().BeTrue();
            _monitor.Cancel(running.Id).Should().BeTrue();
            cancellation.IsCancellationRequested.Should().BeTrue();
        }

        _monitor.IsInProcess(running.Id).Should().BeFalse();
        _monitor.Cancel(running.Id).Should().BeFalse();
    }

    [Fact]
    public async Task AQueuedRunWithoutProcess_IsGivenUp_OnlyAfterTheGrace()
    {
        var queued = Execution(CommandMode.Terminal);

        await _monitor.ReconcileAsync(Ct);
        queued.Status.Should().Be(CommandExecutionStatus.Queued);

        _time.Advance(TimeSpan.FromMinutes(3));
        await _monitor.ReconcileAsync(Ct);

        queued.Status.Should().Be(CommandExecutionStatus.Failed);
    }

    [Fact]
    public async Task TheProcessExiting_EndsTheExecution_WithItsExitCode()
    {
        var once = Terminal(24680, alive: true, keepOpen: false);

        using (_monitor)
        {
            _monitor.Watch(CommandExecutionWatch.For(once)!);

            _processes.Exit(24680, exitCode: 3);
            await _runner.Idle();

            once.Status.Should().Be(CommandExecutionStatus.Failed);
            once.ExitCode.Should().Be(3);
            _monitor.WatchCount.Should().Be(0);
            _changed.Should().Equal(_taskId);
        }
    }

    [Fact]
    public void WatchingTwice_KeepsOneWatch()
    {
        var open = Terminal(24680, alive: true);
        var watch = CommandExecutionWatch.For(open)!;

        using (_monitor)
        {
            _monitor.Watch(watch);
            _monitor.Watch(watch);

            _monitor.WatchCount.Should().Be(1);
        }
    }

    [Fact]
    public void Disposing_StopsWatching_WithoutTouchingTheTerminal()
    {
        var open = Terminal(24680, alive: true);

        _monitor.Watch(CommandExecutionWatch.For(open)!);
        _monitor.Dispose();

        _processes.DisposedWatches.Should().Be(1);
        _processes.IsAlive(24680, Now).Should().BeTrue();
        open.Status.Should().Be(CommandExecutionStatus.Running);
    }

    [Fact]
    public void AFinishedExecution_HasNothingToWatch()
    {
        var execution = Execution(CommandMode.Execute);

        CommandExecutionWatch.For(execution).Should().BeNull();
    }

    /// <summary>Roda os handlers na hora, e deixa esperar pelos disparados em segundo plano.</summary>
    private sealed class InlineUseCaseRunner : IUseCaseRunner
    {
        private readonly Dictionary<Type, Func<object>> _factories = [];
        private readonly List<Task> _running = [];

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
