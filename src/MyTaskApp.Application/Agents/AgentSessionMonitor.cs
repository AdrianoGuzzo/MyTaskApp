using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Acompanha os processos das sessões de agente e mantém o banco em dia com
/// eles (ADR-030).
/// </summary>
/// <remarks>
/// <para>
/// <b>Por evento, não por polling:</b> cada sessão viva tem um vigia que avisa
/// quando o processo sai (<see cref="IAgentProcessTracker.WatchExit"/>). O timer
/// existe só como rede de segurança — um aviso perdido, uma sessão gravada por
/// outra instância — e tem cadência de minuto, não de segundo.
/// </para>
/// <para>
/// <b>Primeiro tique imediato,</b> como os outros laços (ADR-015): é ele que
/// recupera as sessões de antes de o app ser reaberto.
/// </para>
/// <para>
/// <b>Descartar não encerra o agente.</b> Fechar o app só para de vigiar: o
/// Claude continua no terminal, e a próxima abertura o reencontra pelo PID.
/// </para>
/// </remarks>
public sealed class AgentSessionMonitor(
    IUseCaseRunner runner,
    IAgentProcessTracker processes,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options,
    ILogger<AgentSessionMonitor> logger) : IAgentSessionWatcher, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();

    // Espera zero, como nos outros laços: um tique em andamento faz o próximo
    // ser pulado, não enfileirado.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, IDisposable> _watches = [];

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>
    /// A sessão de uma tarefa mudou. Chega de qualquer thread — quem desenha
    /// precisa voltar para a de UI.
    /// </summary>
    public event Action<Guid>? SessionsChanged;

    /// <summary>Quantos processos estão sendo vigiados agora — para os testes.</summary>
    public int WatchCount
    {
        get
        {
            lock (_lock)
            {
                return _watches.Count;
            }
        }
    }

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        var period = options.Value.ToAgentSessionReconcilePeriod();

        _timer = timeProvider.CreateTimer(
            _ => _ = RunTickAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: period);

        logger.LogInformation("AgentSessionMonitorStarted {Period}", period);
    }

    /// <summary>Público para que os testes rodem um tique sem depender do timer.</summary>
    public async Task<AgentReconciliation> TickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return AgentReconciliation.Nothing;
        }

        try
        {
            return await runner.RunAsync<ReconcileAgentSessionsHandler, AgentReconciliation>(
                (handler, token) => handler.HandleAsync(new ReconcileAgentSessions(), token),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Watch(AgentSessionWatch watch)
    {
        lock (_lock)
        {
            if (_disposed || _watches.ContainsKey(watch.SessionId))
            {
                return;
            }

            // Guarda o lugar antes de armar: o aviso pode chegar antes de
            // WatchExit voltar, e ele precisa achar (e tirar) a entrada.
            _watches[watch.SessionId] = NoWatch.Instance;
        }

        var handle = processes.WatchExit(watch.ProcessId, watch.ProcessStartedAt, () => OnExited(watch));

        if (handle is null)
        {
            OnExited(watch);
            return;
        }

        lock (_lock)
        {
            if (!_disposed && _watches.TryGetValue(watch.SessionId, out var current) && current == NoWatch.Instance)
            {
                _watches[watch.SessionId] = handle;
                return;
            }
        }

        // Saiu enquanto armava, ou o app está fechando.
        handle.Dispose();
    }

    public void NotifyChanged(Guid taskId)
    {
        try
        {
            SessionsChanged?.Invoke(taskId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentSessionChangedHandlerFailed {TaskId}", taskId);
        }
    }

    private void OnExited(AgentSessionWatch watch)
    {
        IDisposable? handle;

        lock (_lock)
        {
            if (_disposed || !_watches.Remove(watch.SessionId, out handle))
            {
                return;
            }
        }

        handle.Dispose();

        _ = EndAsync(watch);
    }

    private async Task EndAsync(AgentSessionWatch watch)
    {
        try
        {
            await runner.RunAsync<EndAgentSessionHandler>(
                (handler, token) => handler.HandleAsync(new EndAgentSession(watch.SessionId), token),
                _stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Desligando: a próxima abertura reconcilia.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentSessionEndFailed {TaskId} {SessionId}", watch.TaskId, watch.SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }

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

    /// <summary>Também síncrono, pela mesma razão dos outros laços (ADR-015).</summary>
    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        _stopping.Cancel();
        _timer?.Dispose();
        _timer = null;

        if (_gate.Wait(ShutdownGrace))
        {
            _gate.Release();
        }

        Cleanup();
    }

    /// <summary>Solta os vigias — sem encerrar processo nenhum.</summary>
    private bool BeginDispose()
    {
        List<IDisposable> watches;

        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            watches = [.. _watches.Values];
            _watches.Clear();
        }

        foreach (var watch in watches)
        {
            watch.Dispose();
        }

        return true;
    }

    private void Cleanup()
    {
        _stopping.Dispose();
        _gate.Dispose();
    }

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
        catch (ObjectDisposedException)
        {
            // Tique que chegou depois do descarte.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentSessionReconcileFailed");
        }
    }

    private sealed class NoWatch : IDisposable
    {
        public static readonly NoWatch Instance = new();

        public void Dispose()
        {
        }
    }
}
