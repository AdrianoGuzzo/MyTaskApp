using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// A campainha do servidor (<c>/hubs/remote</c>, ADR-086): só avisa que há o
/// que buscar. O pedido mesmo vem pela fila, assinado; nada que chega por
/// aqui é executado.
/// </summary>
public interface IRemoteHub
{
    /// <summary>Uma operação nova para esta máquina, ou uma entregue que voltou à fila.</summary>
    event Action<Guid>? OperationAvailable;

    /// <summary>Alguém pediu para cancelar uma operação que já foi entregue.</summary>
    event Action<Guid>? CancelRequested;

    /// <summary>Uma aprovação foi respondida ou venceu (o id da aprovação).</summary>
    event Action<Guid>? ApprovalResolved;

    bool IsConnected { get; }

    /// <summary>Conecta, se ainda não; nunca lança — sem hub, a busca de 30 s segue.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Sem hub (testes, celular): só a busca periódica.</summary>
public sealed class NoRemoteHub : IRemoteHub
{
    public event Action<Guid>? OperationAvailable
    {
        add { }
        remove { }
    }

    public event Action<Guid>? CancelRequested
    {
        add { }
        remove { }
    }

    public event Action<Guid>? ApprovalResolved
    {
        add { }
        remove { }
    }

    public bool IsConnected => false;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// O laço da fila no agente (seção 4.2): busca a cada
/// <see cref="RemoteLimits.PollInterval"/> e na hora em que o hub avisa, só
/// enquanto esta máquina aceita ser controlada. Sem WebSocket (um proxy que
/// barra), a operação chega pela busca, em até 30 s.
/// </summary>
/// <remarks>
/// A primeira volta com "Aceitar controle" ligado recupera o diário antes de
/// buscar (4.5): o que rodava quando o agente caiu é encerrado, e o que não
/// foi contado ao servidor, contado.
/// </remarks>
public sealed class RemoteOperationScheduler(
    RemoteOperationDispatcher dispatcher,
    IRemoteHub hub,
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    ILogger<RemoteOperationScheduler> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>Quanto o agente espera as operações em curso ao fechar.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITimer? _timer;
    private bool _recovered;
    private int _again;
    private bool _disposed;

    /// <summary>Quantas voltas terminaram (os testes esperam por elas).</summary>
    public int Ticks { get; private set; }

    public event Action? Ticked;

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        hub.OperationAvailable += OnAvailable;
        hub.CancelRequested += OnAvailable;
        _timer = timeProvider.CreateTimer(_ => _ = TickAsync(), state: null, dueTime: TimeSpan.Zero, period: RemoteLimits.PollInterval);
        logger.LogInformation("RemoteOperationsStarted {Period}", RemoteLimits.PollInterval);
    }

    /// <summary>
    /// Uma volta agora. Pedida durante outra, faz mais uma no fim: um aviso
    /// que chega no meio de uma busca não se perde.
    /// </summary>
    public async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            Interlocked.Exchange(ref _again, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _again, 0);
                await RunOnceAsync(_stopping.Token);
            }
            while (Interlocked.Exchange(ref _again, 0) == 1 && !_stopping.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Fechando.
        }
        catch (Exception exception)
        {
            // Rede, servidor, login: a próxima volta tenta de novo; nada aqui derruba o agente.
            logger.LogWarning(exception, "RemoteOperationsTickFailed");
        }
        finally
        {
            Ticks++;
            _gate.Release();
            Ticked?.Invoke();
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await runner.RunAsync<IRemoteSettingsStore, RemoteSettings>((store, token) => store.GetAsync(token), cancellationToken);

        if (!settings.AcceptControl)
        {
            if (hub.IsConnected)
            {
                await hub.DisconnectAsync(cancellationToken);
            }

            return;
        }

        await hub.ConnectAsync(cancellationToken);

        if (!_recovered)
        {
            await dispatcher.RecoverAsync(cancellationToken);
            _recovered = true;
        }

        await dispatcher.PollAsync(cancellationToken);
    }

    private void OnAvailable(Guid operationId)
    {
        logger.LogDebug("RemoteOperationSignaled {OperationId}", operationId);
        _ = TickAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        hub.OperationAvailable -= OnAvailable;
        hub.CancelRequested -= OnAvailable;

        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        await _stopping.CancelAsync();
        await dispatcher.StopAsync(ShutdownGrace);

        try
        {
            await hub.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "RemoteHubDisconnectFailed");
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// Um contêiner descartado sem <c>await</c> (o desktop, que nunca o
    /// liga) chega aqui: com o laço parado, não há o que esperar.
    /// </summary>
    public void Dispose()
    {
        if (_timer is null)
        {
            _disposed = true;
            return;
        }

        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
