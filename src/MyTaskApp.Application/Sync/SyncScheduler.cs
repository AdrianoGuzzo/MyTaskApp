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
/// <para>
/// <b>Parar e voltar.</b> O desktop liga uma vez e só desliga ao fechar. O
/// celular (ADR-067) para ao ir para segundo plano — o sistema congela o
/// processo de qualquer jeito — e volta com <see cref="Start"/> ao reabrir.
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

    private readonly Lock _gate = new();

    /// <summary>O cancelamento da volta atual do laço; um novo a cada <see cref="Start"/>.</summary>
    private CancellationTokenSource? _stopping;

    private Task? _loop;

    /// <summary>A volta que parou mas ainda não terminou (um push pendurado na rede).</summary>
    private Task? _draining;

    private int _failures;

    private bool _disposed;

    /// <summary>Terminou um ciclo: a tela relê o estado.</summary>
    public event Action<SyncCycleResult>? CycleCompleted;

    /// <summary>Começou um ciclo: o indicador do cabeçalho pode mostrar "sincronizando" (ADR-063).</summary>
    public event Action? CycleStarted;

    /// <summary>
    /// Uma gravação acordou o laço: há algo novo no outbox. Avisa antes da
    /// rajada para a contagem de pendentes subir na hora — sem rede, o próximo
    /// ciclo pode estar a minutos (ADR-063).
    /// </summary>
    public event Action? ChangeQueued;

    public SyncCycleResult? LastResult { get; private set; }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
            {
                return;
            }

            var stopping = new CancellationTokenSource();
            var previous = _draining;
            _stopping = stopping;
            _draining = null;

            // Nunca dois ciclos juntos, nem depois de parar e voltar: a volta
            // anterior que ainda não terminou termina antes desta começar.
            _loop = Task.Run(async () =>
            {
                await DrainAsync(previous);
                await LoopAsync(stopping.Token);
            });
        }
    }

    /// <summary>
    /// Para o laço e espera um pouco o ciclo em andamento (ADR-067). Pode voltar
    /// com <see cref="Start"/>; o que não foi enviado continua no outbox.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task? loop;

        lock (_gate)
        {
            stopping = _stopping;
            loop = _loop;
            _stopping = null;
            _loop = null;
            _draining = loop;
        }

        if (stopping is null)
        {
            return;
        }

        await stopping.CancelAsync();
        await WaitBrieflyAsync(loop);
        stopping.Dispose();
    }

    /// <summary>
    /// Para o laço, espera o ciclo em andamento terminar de vez, roda a ação e
    /// volta, se estava rodando (troca de servidor, ADR-078). Diferente do
    /// <see cref="StopAsync"/>, não desiste de esperar: a ação mexe no que o
    /// ciclo grava.
    /// </summary>
    public async Task RunPausedAsync(Func<Task> action)
    {
        bool wasRunning;

        lock (_gate)
        {
            wasRunning = _loop is not null;
        }

        await StopAsync();

        Task? draining;

        lock (_gate)
        {
            draining = _draining;
        }

        await DrainAsync(draining);

        try
        {
            await action();
        }
        finally
        {
            if (wasRunning)
            {
                Start();
            }
        }
    }

    private static async Task DrainAsync(Task? previous)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            await previous;
        }
        catch (Exception exception) when (exception is OperationCanceledException)
        {
            // Cancelada ao parar: é o esperado.
        }
    }

    private static async Task WaitBrieflyAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(ShutdownWait);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // O encerramento não espera um push pendurado na rede.
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
                    ChangeQueued?.Invoke();

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
            CycleStarted?.Invoke();
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
        if (!TryClaimDisposal(out var stopping, out var loop))
        {
            return;
        }

        stopping?.Cancel();

        try
        {
            loop?.Wait(ShutdownWait);
        }
        catch (AggregateException)
        {
            // Cancelado no meio de um ciclo: o outbox guarda o que faltou.
        }

        stopping?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryClaimDisposal(out var stopping, out var loop))
        {
            return;
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync();
        }

        await WaitBrieflyAsync(loop);
        stopping?.Dispose();
    }

    private bool TryClaimDisposal(out CancellationTokenSource? stopping, out Task? loop)
    {
        lock (_gate)
        {
            stopping = _stopping;
            loop = _loop;

            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            _stopping = null;
            _loop = null;
            return true;
        }
    }
}
