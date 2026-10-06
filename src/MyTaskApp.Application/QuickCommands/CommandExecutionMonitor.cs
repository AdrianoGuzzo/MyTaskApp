using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>O que os casos de uso dizem ao monitor dos comandos rápidos (ADR-051).</summary>
public interface ICommandExecutionWatcher
{
    /// <summary>Vigia o terminal aberto: quando o processo sair, a execução termina.</summary>
    void Watch(CommandExecutionWatch watch);

    /// <summary>Uma execução da tarefa mudou: a tela que a mostra recarrega.</summary>
    void NotifyChanged(Guid taskId);

    /// <summary>
    /// Marca uma execução escondida como rodando <b>neste</b> processo, até o
    /// retorno ser descartado. A reconciliação não a toma por interrompida, e
    /// <see cref="Cancel"/> a alcança.
    /// </summary>
    IDisposable TrackInProcess(Guid executionId, CancellationTokenSource cancellation);

    bool IsInProcess(Guid executionId);

    /// <summary>Cancela uma execução escondida que roda neste processo. <c>false</c> se ela não está aqui.</summary>
    bool Cancel(Guid executionId);
}

public sealed record CommandExecutionWatch(Guid ExecutionId, Guid TaskId, int ProcessId, DateTimeOffset ProcessStartedAt)
{
    /// <summary><c>null</c> quando não há o que vigiar: sem processo, ou já terminada.</summary>
    public static CommandExecutionWatch? For(CommandExecution execution) =>
        execution is { Status: CommandExecutionStatus.Running, ProcessId: { } processId, ProcessStartedAt: { } startedAt }
            ? new CommandExecutionWatch(execution.Id, execution.TaskItemId, processId, startedAt)
            : null;
}

/// <summary>
/// Acompanha os comandos rápidos em andamento (ADR-051), no molde do monitor do
/// agente (ADR-030): um vigia por terminal aberto, sem polling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem timer.</b> O agente tem uma rede de segurança de minuto em minuto; aqui
/// basta reconciliar ao abrir o app (<see cref="Start"/>) e a cada leitura da
/// tela (<see cref="GetQuickCommandsHandler"/>). O que um timer pegaria a mais —
/// um aviso de saída perdido — a tela pega na próxima vez que olha.
/// </para>
/// <para>
/// <b>O registro em processo</b> é o que separa "rodando aqui" de "o app caiu
/// com ele rodando": uma execução escondida não tem PID gravado. É também o
/// gancho de um "Parar" futuro, local ou remoto.
/// </para>
/// <para>
/// <b>Descartar não encerra nada</b>: um <c>dotnet run</c> aberto continua no
/// terminal, e a próxima abertura o reencontra pelo PID e pelo início.
/// </para>
/// </remarks>
public sealed class CommandExecutionMonitor(
    IUseCaseRunner runner,
    IAgentProcessTracker processes,
    ILogger<CommandExecutionMonitor> logger) : ICommandExecutionWatcher, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, IDisposable> _watches = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _inProcess = [];

    private bool _started;
    private bool _disposed;

    /// <summary>As execuções de uma tarefa mudaram. Chega de qualquer thread.</summary>
    public event Action<Guid>? ExecutionsChanged;

    /// <summary>Quantos terminais estão sendo vigiados agora — para os testes.</summary>
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

    /// <summary>Reconcilia uma vez, em segundo plano: é o que reencontra os terminais de antes de o app abrir.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _ = RunReconcileAsync();
    }

    /// <summary>Público para que os testes reconciliem sem depender do <see cref="Start"/>.</summary>
    public Task<CommandReconciliation> ReconcileAsync(CancellationToken cancellationToken) =>
        runner.RunAsync<ReconcileCommandExecutionsHandler, CommandReconciliation>(
            (handler, token) => handler.HandleAsync(new ReconcileCommandExecutions(), token),
            cancellationToken);

    public void Watch(CommandExecutionWatch watch)
    {
        lock (_lock)
        {
            if (_disposed || _watches.ContainsKey(watch.ExecutionId))
            {
                return;
            }

            // Guarda o lugar antes de armar: o aviso pode chegar antes de
            // WatchExitCode voltar, e ele precisa achar (e tirar) a entrada.
            _watches[watch.ExecutionId] = NoWatch.Instance;
        }

        var handle = processes.WatchExitCode(
            watch.ProcessId,
            watch.ProcessStartedAt,
            exitCode => OnExited(watch, exitCode));

        if (handle is null)
        {
            OnExited(watch, null);
            return;
        }

        lock (_lock)
        {
            if (!_disposed && _watches.TryGetValue(watch.ExecutionId, out var current) && current == NoWatch.Instance)
            {
                _watches[watch.ExecutionId] = handle;
                return;
            }
        }

        handle.Dispose();
    }

    public void NotifyChanged(Guid taskId)
    {
        try
        {
            ExecutionsChanged?.Invoke(taskId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CommandExecutionChangedHandlerFailed {TaskId}", taskId);
        }
    }

    public IDisposable TrackInProcess(Guid executionId, CancellationTokenSource cancellation)
    {
        lock (_lock)
        {
            _inProcess[executionId] = cancellation;
        }

        return new InProcessRegistration(this, executionId);
    }

    public bool IsInProcess(Guid executionId)
    {
        lock (_lock)
        {
            return _inProcess.ContainsKey(executionId);
        }
    }

    public bool Cancel(Guid executionId)
    {
        CancellationTokenSource? cancellation;

        lock (_lock)
        {
            if (!_inProcess.TryGetValue(executionId, out cancellation))
            {
                return false;
            }
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Untrack(Guid executionId)
    {
        lock (_lock)
        {
            _inProcess.Remove(executionId);
        }
    }

    private void OnExited(CommandExecutionWatch watch, int? exitCode)
    {
        IDisposable? handle;

        lock (_lock)
        {
            if (_disposed || !_watches.Remove(watch.ExecutionId, out handle))
            {
                return;
            }
        }

        handle.Dispose();

        _ = EndAsync(watch, exitCode);
    }

    private async Task EndAsync(CommandExecutionWatch watch, int? exitCode)
    {
        try
        {
            await runner.RunAsync<EndCommandExecutionHandler>(
                (handler, token) => handler.HandleAsync(new EndCommandExecution(watch.ExecutionId, exitCode), token),
                _stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Desligando: a próxima abertura reconcilia.
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "CommandExecutionEndFailed {TaskId} {ExecutionId}",
                watch.TaskId,
                watch.ExecutionId);
        }
    }

    private async Task RunReconcileAsync()
    {
        try
        {
            var result = await ReconcileAsync(_stopping.Token);

            logger.LogInformation(
                "CommandExecutionsReconciled {Alive} {Ended}",
                result.Alive,
                result.Ended);
        }
        catch (OperationCanceledException)
        {
            // Desligando: normal.
        }
        catch (ObjectDisposedException)
        {
            // Chegou depois do descarte.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CommandExecutionReconcileFailed");
        }
    }

    /// <summary>Solta os vigias — sem encerrar processo nenhum.</summary>
    public void Dispose()
    {
        List<IDisposable> watches;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watches = [.. _watches.Values];
            _watches.Clear();
        }

        _stopping.Cancel();

        foreach (var watch in watches)
        {
            watch.Dispose();
        }

        _stopping.Dispose();
    }

    private sealed class InProcessRegistration(CommandExecutionMonitor owner, Guid executionId) : IDisposable
    {
        public void Dispose() => owner.Untrack(executionId);
    }

    private sealed class NoWatch : IDisposable
    {
        public static readonly NoWatch Instance = new();

        public void Dispose()
        {
        }
    }
}
