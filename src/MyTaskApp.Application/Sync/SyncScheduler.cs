using Microsoft.Extensions.Logging;

namespace MyTaskApp.Application.Sync;

/// <summary>Os tempos do agendador (ADR-061).</summary>
public sealed record SyncSchedule(TimeSpan Debounce, TimeSpan PollInterval, TimeSpan BackoffBase, TimeSpan BackoffCap)
{
    /// <summary>3 s depois de uma gravação; a cada minuto parado; espera até 5 min quando falha.</summary>
    public static SyncSchedule Default { get; } =
        new(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Espera exponencial com "full jitter": um valor sorteado entre zero e o
    /// teto da tentativa. Dois computadores que caíram juntos não voltam juntos.
    /// </summary>
    public TimeSpan Backoff(int attempt, double random, TimeSpan? retryAfter)
    {
        var ceiling = Math.Min(BackoffCap.TotalMilliseconds, BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30)));
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(random, 0, 1) * ceiling);

        return retryAfter is { } asked && asked > delay ? asked : delay;
    }
}

/// <summary>
/// Quem roda a sincronização em segundo plano (ADR-061): um laço só, que
/// acorda pouco depois de uma gravação, a cada minuto parado, e espera mais a
/// cada falha passageira.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nunca dois ciclos juntos.</b> O laço é um só, e o "Sincronizar agora"
/// da tela só o acorda — se um ciclo estiver no meio, o próximo vem logo
/// depois, e não em paralelo.
/// </para>
/// <para>
/// <b>Encerrar é cancelar e esperar um pouco</b>, como os outros agendadores
/// (ADR-015): um push interrompido no meio é reenviado no próximo início com
/// os mesmos ids, e o servidor reconhece.
/// </para>
/// </remarks>
public sealed class SyncScheduler(
    ISyncCycleRunner engine,
    ISyncTrigger trigger,
    TimeProvider timeProvider,
    ILogger<SyncScheduler> logger,
    SyncSchedule? schedule = null,
    Func<double>? random = null) : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    /// <summary>Bloqueado não tem polling: o laço só acorda com um aviso.</summary>
    public static readonly TimeSpan BlockedWait = TimeSpan.FromDays(1);

    private readonly SyncSchedule _schedule = schedule ?? SyncSchedule.Default;

    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;

    private int _failures;

    private bool _disposed;

    /// <summary>Terminou um ciclo: a tela relê o estado.</summary>
    public event Action<SyncCycleResult>? CycleCompleted;

    public SyncCycleResult? LastResult { get; private set; }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (_loop is null && !_disposed)
        {
            _loop = Task.Run(() => LoopAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// "Sincronizar agora": acorda o laço sem a espera da rajada; não roda em
    /// paralelo a um ciclo em andamento — vem logo depois dele.
    /// </summary>
    public void RequestNow()
    {
        _failures = 0;
        Volatile.Write(ref _immediate, true);
        trigger.Signal();
    }

    private bool _immediate;

    private async Task LoopAsync(CancellationToken stopping)
    {
        var delay = TimeSpan.Zero;

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (await WaitAsync(delay, stopping) && !Interlocked.Exchange(ref _immediate, false))
                {
                    // Uma gravação acordou o laço: espera as próximas da mesma rajada.
                    await Task.Delay(_schedule.Debounce, timeProvider, stopping);
                }

                Volatile.Write(ref _immediate, false);

                delay = await RunCycleAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // O laço não morre: um erro inesperado vira uma espera, como a rede caída.
                logger.LogError(exception, "SyncLoopFailed");
                delay = _schedule.Backoff(++_failures, _random(), null);
            }
        }
    }

    /// <summary>Espera o tempo ou um aviso de gravação; devolve se foi o aviso.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stopping)
    {
        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var signal = trigger.WaitAsync(waiting.Token);
        var timer = Task.Delay(delay, timeProvider, waiting.Token);

        var first = await Task.WhenAny(signal, timer);
        await waiting.CancelAsync();
        stopping.ThrowIfCancellationRequested();

        return first == signal && signal.IsCompletedSuccessfully;
    }

    private async Task<TimeSpan> RunCycleAsync(CancellationToken stopping)
    {
        IsRunning = true;
        SyncCycleResult result;

        try
        {
            result = await engine.RunCycleAsync(stopping);
        }
        finally
        {
            IsRunning = false;
        }

        LastResult = result;
        CycleCompleted?.Invoke(result);

        switch (result.Status)
        {
            case SyncCycleStatus.Transient:
                var wait = _schedule.Backoff(++_failures, _random(), result.RetryAfter);
                logger.LogInformation("SyncBackoff {Attempt} {DelayMs:0}", _failures, wait.TotalMilliseconds);
                return wait;

            case SyncCycleStatus.Blocked:
                // Precisa de alguém: só volta com um aviso (conectar de novo, "Sincronizar agora").
                _failures = 0;
                return BlockedWait;

            default:
                _failures = 0;
                return _schedule.PollInterval;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();

        try
        {
            _loop?.Wait(ShutdownWait);
        }
        catch (AggregateException)
        {
            // Cancelado no meio de um ciclo: o outbox guarda o que faltou.
        }

        _stopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ShutdownWait);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // O encerramento não espera um push pendurado na rede.
            }
        }

        _stopping.Dispose();
    }
}
