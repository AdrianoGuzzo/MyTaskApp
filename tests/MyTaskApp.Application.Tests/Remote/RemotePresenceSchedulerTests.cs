using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>O sinal de vida de cada minuto (T-06): registra ao ligar, de novo a cada período, e sobrevive a um tique que falha.</summary>
public sealed class RemotePresenceSchedulerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private readonly IUseCaseRunner _runner = Substitute.For<IUseCaseRunner>();

    private int _registrations;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RemotePresenceSchedulerTests() =>
        _runner.RunAsync(Arg.Any<Func<RegisterThisMachineHandler, CancellationToken, Task<RemoteAvailability>>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _registrations);
                return RemoteAvailability.Ready;
            });

    private RemotePresenceScheduler Scheduler() => new(_runner, _time, NullLogger<RemotePresenceScheduler>.Instance);

    [Fact]
    public async Task Starting_RegistersRightAway_AndAgainEveryPeriod()
    {
        await using var scheduler = Scheduler();

        scheduler.Start();
        scheduler.Start();
        await WaitForAsync(1);

        _time.Advance(RemotePresenceScheduler.Period);
        await WaitForAsync(2);

        scheduler.Last.Should().Be(RemoteAvailability.Ready);
        RemotePresenceScheduler.Period.Should().BeLessThan(TimeSpan.FromSeconds(90), "um tique perdido não pode derrubar a máquina na lista");
    }

    [Fact]
    public async Task ATickNow_RegistersWithoutWaitingTheMinute()
    {
        await using var scheduler = Scheduler();

        (await scheduler.TickAsync(Ct)).Should().Be(RemoteAvailability.Ready);
        _registrations.Should().Be(1);
    }

    [Fact]
    public async Task ATickThatThrows_DoesNotKillTheTimer()
    {
        _runner.RunAsync(Arg.Any<Func<RegisterThisMachineHandler, CancellationToken, Task<RemoteAvailability>>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                {
                    Interlocked.Increment(ref _registrations);
                    throw new InvalidOperationException("banco ocupado");
                },
                _ =>
                {
                    Interlocked.Increment(ref _registrations);
                    return Task.FromResult(RemoteAvailability.Ready);
                });
        await using var scheduler = Scheduler();

        scheduler.Start();
        await WaitForAsync(1);
        _time.Advance(RemotePresenceScheduler.Period);
        await WaitForAsync(2);

        scheduler.Last.Should().Be(RemoteAvailability.Ready);
    }

    [Fact]
    public void Disposing_Twice_AndSynchronously_IsFine()
    {
        var scheduler = Scheduler();
        scheduler.Start();

        scheduler.Dispose();
        scheduler.Dispose();

        var registered = _registrations;
        _time.Advance(RemotePresenceScheduler.Period);
        _registrations.Should().BeLessThanOrEqualTo(Math.Max(registered, 1));
    }

    [Fact]
    public async Task AfterDisposing_StartDoesNothing()
    {
        var scheduler = Scheduler();
        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();

        scheduler.Start();
        _time.Advance(RemotePresenceScheduler.Period);

        _registrations.Should().Be(0);
    }

    private async Task WaitForAsync(int registrations)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (Volatile.Read(ref _registrations) < registrations)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "o laço devia ter registrado");
            await Task.Delay(10, Ct);
        }
    }
}
