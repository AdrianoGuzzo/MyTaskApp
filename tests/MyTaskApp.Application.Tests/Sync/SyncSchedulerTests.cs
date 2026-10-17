using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Application.Tests.Sync;

/// <summary>
/// Os testes que medem tempo com o relógio falso e um laço em outra thread:
/// sozinhos, sem outras classes disputando a thread pool. Com a suíte em
/// paralelo, a continuação do timer passava dos 3 ms de cada passo e o relógio
/// andava segundos a mais.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class SchedulerTimingCollection;

/// <summary>
/// O agendador da sincronização (ADR-061): quando roda, quanto espera, e que
/// nunca roda dois ciclos juntos.
/// </summary>
[Collection(typeof(SchedulerTimingCollection))]
public sealed class SyncSchedulerTests : IAsyncDisposable
{
    private static readonly SyncSchedule Schedule = new(
        Debounce: TimeSpan.FromSeconds(3),
        PollInterval: TimeSpan.FromSeconds(60),
        BackoffBase: TimeSpan.FromSeconds(2),
        BackoffCap: TimeSpan.FromMinutes(5));

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    private readonly SyncTrigger _trigger = new();

    private readonly ScriptedCycle _cycle = new();

    private SyncScheduler? _scheduler;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(1, 1.0, 4)]
    [InlineData(2, 1.0, 8)]
    [InlineData(10, 1.0, 300)]
    [InlineData(3, 0.0, 0)]
    [InlineData(3, 0.5, 8)]
    public void TheBackoff_GrowsExponentially_WithJitter_UpToTheCap(int attempt, double random, double seconds)
    {
        Schedule.Backoff(attempt, random, retryAfter: null).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void TheServersRetryAfter_IsAFloor()
    {
        Schedule.Backoff(1, 0.0, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        Schedule.Backoff(10, 1.0, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ItRunsAtStart_AndThenPollsEveryMinute()
    {
        Start();
        await WaitForCyclesAsync(1);

        ShouldBeAbout(await AdvanceUntilAsync(2), 60, "parado, um ciclo por minuto");
    }

    [Fact]
    public async Task ALocalChange_WakesItUp_AfterTheDebounce()
    {
        Start();
        await WaitForCyclesAsync(1);

        _trigger.Signal();

        ShouldBeAbout(await AdvanceUntilAsync(2), 3, "espera a rajada de gravações terminar, e não o minuto");
    }

    [Fact]
    public async Task ATransientFailure_WaitsTheBackoff_AndSuccessResetsIt()
    {
        _cycle.Next.Enqueue(new SyncCycleResult(SyncCycleStatus.Transient));
        _cycle.Next.Enqueue(new SyncCycleResult(SyncCycleStatus.Transient));
        Start(random: 0.5);
        await WaitForCyclesAsync(1);

        ShouldBeAbout(await AdvanceUntilAsync(2), 2, "tentativa 1: metade de 4 s");
        ShouldBeAbout(await AdvanceUntilAsync(3), 4, "tentativa 2: metade de 8 s");
        ShouldBeAbout(await AdvanceUntilAsync(4), 60, "deu certo: volta ao minuto");
    }

    [Fact]
    public async Task WhenBlocked_ItStopsPolling_UntilSomeoneAsks()
    {
        _cycle.Next.Enqueue(new SyncCycleResult(SyncCycleStatus.Blocked));
        Start();
        await WaitForCyclesAsync(1);

        _clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, Ct);
        _cycle.Runs.Should().Be(1);

        _scheduler!.RequestNow();
        await WaitForCyclesAsync(2);
    }

    [Fact]
    public async Task TwoCyclesNeverRunTogether()
    {
        _cycle.Gate = new TaskCompletionSource();
        Start();
        await WaitUntilAsync(() => _cycle.Running == 1);

        for (var i = 0; i < 5; i++)
        {
            _scheduler!.RequestNow();
        }

        await Task.Delay(50, Ct);
        _cycle.MaxConcurrent.Should().Be(1);

        _cycle.Gate.SetResult();
        await WaitForCyclesAsync(2);
        _cycle.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_CancelsTheCycleInFlight()
    {
        _cycle.Gate = new TaskCompletionSource();
        Start();
        await WaitUntilAsync(() => _cycle.Running == 1);

        await _scheduler!.DisposeAsync();

        _cycle.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task StoppingInTheBackground_CancelsTheCycle_AndStartingAgainRunsAtOnce()
    {
        _cycle.Gate = new TaskCompletionSource();
        Start();
        await WaitUntilAsync(() => _cycle.Running == 1);

        await _scheduler!.StopAsync();

        _cycle.WasCancelled.Should().BeTrue();

        // O próximo ciclo não fica preso no portão do que foi cancelado.
        _cycle.Gate!.SetResult();
        _scheduler.Start();
        await WaitForCyclesAsync(2);
    }

    [Fact]
    public async Task WhileStopped_NothingRuns_EvenWhenAsked()
    {
        Start();
        await WaitForCyclesAsync(1);
        await WaitUntilAsync(() => _cycle.Running == 0);

        await _scheduler!.StopAsync();
        _scheduler.RequestNow();
        _trigger.Signal();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, Ct);

        _cycle.Runs.Should().Be(1);

        _scheduler.Start();
        await WaitForCyclesAsync(2);
    }

    [Fact]
    public async Task StoppingTwice_OrDisposingAfterStopping_IsHarmless()
    {
        Start();
        await WaitForCyclesAsync(1);

        await _scheduler!.StopAsync();
        await _scheduler.StopAsync();
        await _scheduler.DisposeAsync();

        _scheduler.Start();
        await Task.Delay(50, Ct);
        _cycle.Runs.Should().Be(1, "descartado não volta");
    }

    [Fact]
    public async Task ACycleStuckOnTheNetwork_FinishesBeforeTheNextStartRuns()
    {
        var stuck = new TaskCompletionSource();
        _cycle.Stubborn = stuck;
        Start();
        await WaitUntilAsync(() => _cycle.Running == 1);

        // O ciclo ignora o cancelamento: parar desiste de esperar depois de um tempo.
        await _scheduler!.StopAsync();
        _scheduler.Start();
        await Task.Delay(100, Ct);

        _cycle.Runs.Should().Be(0, "a volta nova espera a anterior");
        _cycle.MaxConcurrent.Should().Be(1);

        stuck.SetResult();
        await WaitForCyclesAsync(2);
        _cycle.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task EveryCycle_IsReported()
    {
        var reported = new List<SyncCycleResult>();
        Start();
        _scheduler!.CycleCompleted += reported.Add;
        _clock.Advance(TimeSpan.FromSeconds(60));

        await WaitUntilAsync(() => reported.Count >= 1);
        _scheduler.LastResult.Should().NotBeNull();
    }

    [Fact]
    public async Task TheStartOfACycle_IsReported_WhileItRuns()
    {
        _cycle.Gate = new TaskCompletionSource();
        var runningWhenStarted = new List<bool>();
        _scheduler = new SyncScheduler(_cycle, _trigger, _clock, NullLogger<SyncScheduler>.Instance, Schedule, () => 1.0);
        _scheduler.CycleStarted += () => runningWhenStarted.Add(_scheduler.IsRunning);
        _scheduler.Start();

        await WaitUntilAsync(() => _cycle.Running == 1);

        runningWhenStarted.Should().Equal(true);
        _cycle.Gate.SetResult();
    }

    [Fact]
    public async Task ALocalChange_IsReported_BeforeTheDebounce()
    {
        var queued = 0;
        Start();
        _scheduler!.ChangeQueued += () => Interlocked.Increment(ref queued);
        await WaitForCyclesAsync(1);
        await WaitUntilAsync(() => _cycle.Running == 0);
        await Task.Delay(30, Ct);

        _trigger.Signal();

        await WaitUntilAsync(() => Volatile.Read(ref queued) == 1);
        _cycle.Runs.Should().Be(1, "o ciclo só vem depois da rajada");
    }

    private void Start(double random = 1.0)
    {
        _scheduler = new SyncScheduler(_cycle, _trigger, _clock, NullLogger<SyncScheduler>.Instance, Schedule, () => random);
        _scheduler.Start();
    }

    private Task WaitForCyclesAsync(int runs) => WaitUntilAsync(() => _cycle.Runs >= runs);

    /// <summary>
    /// Avança o relógio falso de 1 s em 1 s até o ciclo rodar, dando tempo
    /// real ao laço entre os passos; devolve quanto tempo falso foi preciso.
    /// </summary>
    private async Task<TimeSpan> AdvanceUntilAsync(int runs)
    {
        // O laço precisa ter criado a espera antes do primeiro passo.
        await WaitUntilAsync(() => _cycle.Running == 0);
        await Task.Delay(30, Ct);

        var waited = TimeSpan.Zero;

        while (_cycle.Runs < runs)
        {
            waited.Should().BeLessThan(TimeSpan.FromMinutes(10), "o agendador deveria ter rodado");
            _clock.Advance(TimeSpan.FromSeconds(1));
            waited += TimeSpan.FromSeconds(1);

            // A continuação do timer roda no pool: alguns milissegundos bastam,
            // e a tolerância das asserções cobre um passo a mais.
            var spin = System.Diagnostics.Stopwatch.StartNew();

            while (_cycle.Runs < runs && spin.ElapsedMilliseconds < 3)
            {
                Thread.Yield();
            }
        }

        await Task.Yield();
        return waited;
    }

    private static void ShouldBeAbout(TimeSpan waited, double seconds, string because) =>
        waited.TotalSeconds.Should().BeInRange(seconds, seconds + 2, because);

    /// <summary>O laço roda em outra thread: espera de verdade, com limite, o que o relógio falso liberou.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "o agendador deveria ter rodado");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class ScriptedCycle : ISyncCycleRunner
    {
        private int _running;

        public Queue<SyncCycleResult> Next { get; } = new();

        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Uma espera que ignora o cancelamento: o push pendurado na rede.</summary>
        public TaskCompletionSource? Stubborn { get; set; }

        public int Runs { get; private set; }

        public int Running => _running;

        public int MaxConcurrent { get; private set; }

        public bool WasCancelled { get; private set; }

        public async Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _running);
            MaxConcurrent = Math.Max(MaxConcurrent, now);

            try
            {
                if (Gate is { } gate)
                {
                    try
                    {
                        await gate.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        WasCancelled = true;
                        throw;
                    }

                    Gate = null;
                }

                if (Stubborn is { } stubborn)
                {
                    Stubborn = null;
                    await stubborn.Task;
                }

                return Next.TryDequeue(out var result) ? result : SyncCycleResult.Completed;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                Runs++;
            }
        }
    }
}
