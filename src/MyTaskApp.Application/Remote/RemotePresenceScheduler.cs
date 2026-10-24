using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O sinal de vida desta máquina na orquestração (T-06): a cada minuto,
/// registra de novo, e as outras a veem ligada (offline depois de 90 s sem
/// notícia). Com o remoto desligado, o tique só lê a configuração.
/// </summary>
/// <remarks>
/// O mesmo mecanismo do <c>LifecycleMaintenanceScheduler</c>: timer do
/// <see cref="TimeProvider"/>, escopo por tique pelo <see cref="IUseCaseRunner"/>,
/// tique em andamento faz o seguinte ser pulado. É o terceiro laço com essa
/// mecânica; a base comum que aquele comentário promete fica para um commit
/// só dela, sem misturar com a orquestração. Quando o agente existir (T-10),
/// o sinal de vida passa a ser dele, pelo hub.
/// </remarks>
public sealed class RemotePresenceScheduler(
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    ILogger<RemotePresenceScheduler> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>Bem abaixo dos 90 s do offline: um tique perdido não derruba a máquina na lista.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Como foi o último registro; <c>null</c> antes do primeiro.</summary>
    public RemoteAvailability? Last { get; private set; }

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = timeProvider.CreateTimer(_ => _ = RunTickAsync(), state: null, dueTime: TimeSpan.Zero, period: Period);
        logger.LogInformation("RemotePresenceStarted {Period}", Period);
    }

    /// <summary>Um registro agora: depois de mudar a configuração, sem esperar o minuto.</summary>
    public async Task<RemoteAvailability?> TickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return Last;
        }

        try
        {
            Last = await runner.RunAsync<RegisterThisMachineHandler, RemoteAvailability>(
                (handler, token) => handler.HandleAsync(new RegisterThisMachine(), token),
                cancellationToken);

            return Last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync();

        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }

        if (await _gate.WaitAsync(ShutdownGrace, CancellationToken.None))
        {
            _gate.Release();
        }

        Cleanup();
    }

    /// <summary>Síncrono também, como os outros laços: o contêiner é descartado com um <c>using</c> comum.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _timer?.Dispose();
        _timer = null;

        if (_gate.Wait(ShutdownGrace))
        {
            _gate.Release();
        }

        Cleanup();
    }

    private void Cleanup()
    {
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>Um tique que lança nunca mata o timer: o próximo minuto tenta de novo.</summary>
    private async Task RunTickAsync()
    {
        try
        {
            await TickAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Desligando: normal.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RemotePresenceTickFailed");
        }
    }
}
