using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests.Lifecycle;

/// <summary>
/// O laço da manutenção (§6). Mesmo desenho do <c>ReminderScheduler</c>
/// (ADR-015) e, portanto, as mesmas promessas: primeiro tique imediato, tique
/// sobreposto pulado, e um tique que lança nunca mata o timer.
/// </summary>
public class LifecycleMaintenanceSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Start);
    private readonly CountingUseCaseRunner _runner = new();

    private LifecycleMaintenanceScheduler Scheduler(int minutes = 360) =>
        new(
            _runner,
            _time,
            Options.Create(new ApplicationOptions { LifecycleSweepMinutes = minutes }),
            NullLogger<LifecycleMaintenanceScheduler>.Instance);

    /// <summary>
    /// Um app de desktop passa mais tempo fechado do que aberto. Sem o tique
    /// imediato, a lixeira de quem abre o app uma vez por semana nunca seria
    /// esvaziada.
    /// </summary>
    [Fact]
    public async Task Starting_SweepsImmediatelyToCatchUpOnWhatExpiredWhileClosed()
    {
        await using var scheduler = Scheduler();

        scheduler.Start();

        _runner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task EachPeriod_RunsExactlyOneSweep()
    {
        await using var scheduler = Scheduler();
        scheduler.Start();

        _time.Advance(Period);
        _time.Advance(Period);

        _runner.Invocations.Should().Be(3);
    }

    [Fact]
    public async Task HalfAPeriod_RunsNoSweep()
    {
        await using var scheduler = Scheduler();
        scheduler.Start();

        _time.Advance(TimeSpan.FromHours(3));

        _runner.Invocations.Should().Be(1);
    }

    /// <summary>
    /// Dois lotes concorrentes disputariam as mesmas linhas — e um deles
    /// tentaria apagar o que o outro acabou de apagar.
    /// </summary>
    [Fact]
    public async Task AnOverlappingSweep_IsSkippedNotQueued()
    {
        await using var scheduler = Scheduler();
        _runner.Gate = new TaskCompletionSource();

        var running = scheduler.TickAsync(Ct);
        var overlapping = await scheduler.TickAsync(Ct);

        overlapping.Should().Be(LifecycleMaintenanceResult.Nothing);
        _runner.Invocations.Should().Be(1);

        _runner.Gate.SetResult();
        await running;
    }

    [Fact]
    public async Task ASweepThatThrows_DoesNotStopTheTimer()
    {
        await using var scheduler = Scheduler();
        _runner.Failure = new InvalidOperationException("banco fora do ar");

        scheduler.Start();
        _time.Advance(Period);
        _runner.Failure = null;
        _time.Advance(Period);

        _runner.Invocations.Should().Be(3);
        scheduler.CompletedTicks.Should().Be(3);
    }

    [Fact]
    public async Task StartingTwice_DoesNotDoubleTheSweeping()
    {
        await using var scheduler = Scheduler();

        scheduler.Start();
        scheduler.Start();
        _time.Advance(Period);

        _runner.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_StopsSweeping()
    {
        var scheduler = Scheduler();
        scheduler.Start();

        await scheduler.DisposeAsync();
        _time.Advance(Period);
        _time.Advance(Period);

        _runner.Invocations.Should().Be(1);
    }

    /// <summary>
    /// O composition root descarta o contêiner com um <c>using</c> comum, então
    /// o <c>Dispose()</c> síncrono precisa existir — senão ele lançaria.
    /// </summary>
    [Fact]
    public void TheSynchronousDispose_Works()
    {
        var scheduler = Scheduler();
        scheduler.Start();

        var dispose = scheduler.Dispose;

        dispose.Should().NotThrow();
    }

    [Fact]
    public async Task StartingAfterDispose_DoesNothing()
    {
        var scheduler = Scheduler();
        await scheduler.DisposeAsync();

        scheduler.Start();
        _time.Advance(Period);

        _runner.Invocations.Should().Be(0);
    }

    /// <summary>
    /// Limites defensivos: um intervalo de zero varreria o banco sem parar, e um
    /// de um ano faria a lixeira nunca ser esvaziada.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(999999, 10080)]
    [InlineData(360, 360)]
    public void TheConfiguredPeriod_IsClampedToSomethingSane(int configured, int expected)
    {
        var options = new ApplicationOptions { LifecycleSweepMinutes = configured };

        options.ToLifecycleSweepPeriod().Should().Be(TimeSpan.FromMinutes(expected));
    }
}
