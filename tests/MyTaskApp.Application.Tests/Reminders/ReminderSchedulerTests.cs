using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests.Reminders;

public class ReminderSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Start);
    private readonly CountingUseCaseRunner _runner = new();

    [Fact]
    public async Task Starting_RunsACatchUpTickImmediately()
    {
        // É este tique que recupera o que venceu com o app fechado — sem ele,
        // reabrir o app depois de dias ficaria 30 s em silêncio.
        await using var scheduler = Scheduler();

        scheduler.Start();

        _runner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task EachPeriod_RunsExactlyOneTick()
    {
        await using var scheduler = Scheduler();
        scheduler.Start();

        _time.Advance(Period);
        _time.Advance(Period);

        _runner.Invocations.Should().Be(3);
    }

    [Fact]
    public async Task HalfAPeriod_RunsNoTick()
    {
        await using var scheduler = Scheduler();
        scheduler.Start();

        _time.Advance(TimeSpan.FromSeconds(15));

        _runner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task ATickThatThrows_DoesNotStopTheTimer()
    {
        // Um tique que lança nunca pode matar o agendador: seria o app parar de
        // lembrar em silêncio, que é o pior desfecho possível aqui.
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
    public async Task AnOverlappingTick_IsSkippedNotQueued()
    {
        await using var scheduler = Scheduler();
        _runner.Gate = new TaskCompletionSource();

        var running = scheduler.TickAsync(Ct);
        var overlapping = await scheduler.TickAsync(Ct);

        overlapping.Should().Be(DispatchDueRemindersResult.Nothing);
        _runner.Invocations.Should().Be(1);

        _runner.Gate.SetResult();
        await running;
    }

    [Fact]
    public async Task OnceTheTickFinishes_TheNextOneRunsNormally()
    {
        await using var scheduler = Scheduler();

        await scheduler.TickAsync(Ct);
        await scheduler.TickAsync(Ct);

        _runner.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task StartingTwice_DoesNotDoubleTheTicking()
    {
        await using var scheduler = Scheduler();

        scheduler.Start();
        scheduler.Start();
        _time.Advance(Period);

        _runner.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_StopsTicking()
    {
        var scheduler = Scheduler();
        scheduler.Start();

        await scheduler.DisposeAsync();
        _time.Advance(Period);
        _time.Advance(Period);

        _runner.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task DisposingTwice_IsHarmless()
    {
        var scheduler = Scheduler();
        scheduler.Start();

        await scheduler.DisposeAsync();
        var again = async () => await scheduler.DisposeAsync();

        await again.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartingAfterDispose_DoesNothing()
    {
        var scheduler = Scheduler();
        await scheduler.DisposeAsync();

        scheduler.Start();

        _runner.Invocations.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(30, 30)]
    [InlineData(99999, 3600)]
    public async Task TheConfiguredPeriod_IsClampedToSomethingSane(int configured, int expected)
    {
        // Um tique de 0 s fritaria o disco; um de um dia não é agendador.
        await using var scheduler = Scheduler(configured);
        scheduler.Start();

        _time.Advance(TimeSpan.FromSeconds(expected));

        _runner.Invocations.Should().Be(2);
    }

    private ReminderScheduler Scheduler(int tickSeconds = 30) =>
        new(
            _runner,
            _time,
            Options.Create(new ApplicationOptions { ReminderTickSeconds = tickSeconds }),
            NullLogger<ReminderScheduler>.Instance);
}
