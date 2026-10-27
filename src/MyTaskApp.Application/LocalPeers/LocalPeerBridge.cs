using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Application.LocalPeers;

/// <summary>Um aviso entre o desktop e o agente deste computador (ADR-085). Nada de execução por aqui.</summary>
/// <param name="Areas">Em <see cref="LocalPeerMessageKinds.DataChanged"/>, o que recarregar.</param>
public sealed record LocalPeerMessage(string Kind, DataArea Areas = DataArea.None);

public static class LocalPeerMessageKinds
{
    /// <summary>O outro processo gravou: a tela recarrega o que mudou.</summary>
    public const string DataChanged = "dataChanged";

    /// <summary>O outro processo pôs algo no outbox: quem tem o lease do sync acorda.</summary>
    public const string SyncWanted = "syncWanted";

    /// <summary>A pessoa pediu "Sincronizar agora" no outro processo.</summary>
    public const string SyncNow = "syncNow";

    public static bool IsKnown(string? kind) => kind is DataChanged or SyncWanted or SyncNow;
}

/// <summary>
/// O canal local até o outro processo: o agente escuta, o desktop conecta
/// (named pipe só do usuário; no Linux, Unix socket). Sem o outro lado, enviar
/// não faz nada.
/// </summary>
public interface ILocalPeerChannel : IAsyncDisposable
{
    event Action<LocalPeerMessage>? Received;

    bool IsConnected { get; }

    void Start();

    Task SendAsync(LocalPeerMessage message, CancellationToken cancellationToken);
}

/// <summary>Sem outro processo (o celular, os testes): ninguém para avisar.</summary>
public sealed class NoLocalPeerChannel : ILocalPeerChannel
{
    public event Action<LocalPeerMessage>? Received
    {
        add { }
        remove { }
    }

    public bool IsConnected => false;

    public void Start()
    {
    }

    public Task SendAsync(LocalPeerMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Liga os avisos deste processo aos do outro (ADR-085). O
/// <see cref="IDataChangeNotifier"/>, o <see cref="ISyncTrigger"/> e o "agora"
/// do <see cref="SyncScheduler"/> são de um processo só; daqui eles atravessam,
/// e o que chega do outro lado é repassado sem voltar (sem eco).
/// </summary>
/// <remarks>
/// O desktop grava e o agente, que tem o lease, sincroniza em 3 s, e não no
/// próximo minuto; o agente aplica o que chegou do servidor e a lista do
/// desktop recarrega na hora.
/// </remarks>
public sealed class LocalPeerBridge(
    ILocalPeerChannel channel,
    IDataChangeNotifier notifier,
    ISyncTrigger trigger,
    SyncScheduler scheduler,
    ILogger<LocalPeerBridge> logger) : IAsyncDisposable
{
    /// <summary>O aviso que está sendo repassado agora veio do outro processo: não volta para ele.</summary>
    private static readonly AsyncLocal<bool> Relaying = new();

    private bool _started;

    private bool _disposed;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        notifier.Changed += OnDataChanged;
        trigger.Signaled += OnSyncWanted;
        scheduler.NowRequested += OnSyncNow;
        channel.Received += OnReceived;
        channel.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            notifier.Changed -= OnDataChanged;
            trigger.Signaled -= OnSyncWanted;
            scheduler.NowRequested -= OnSyncNow;
            channel.Received -= OnReceived;
        }

        await channel.DisposeAsync();
    }

    private void OnDataChanged(DataArea areas) => Forward(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, areas));

    private void OnSyncWanted() => Forward(new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted));

    private void OnSyncNow() => Forward(new LocalPeerMessage(LocalPeerMessageKinds.SyncNow));

    private void Forward(LocalPeerMessage message)
    {
        if (Relaying.Value || !channel.IsConnected)
        {
            return;
        }

        _ = SendAsync(message);
    }

    private async Task SendAsync(LocalPeerMessage message)
    {
        try
        {
            await channel.SendAsync(message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // O outro processo saiu no meio: o canal reconecta, e o tique de cada tela cobre o que se perdeu.
            logger.LogDebug(exception, "LocalPeerSendFailed {Kind}", message.Kind);
        }
    }

    private void OnReceived(LocalPeerMessage message)
    {
        Relaying.Value = true;

        try
        {
            switch (message.Kind)
            {
                case LocalPeerMessageKinds.DataChanged:
                    notifier.NotifyChanged(message.Areas);
                    break;

                case LocalPeerMessageKinds.SyncWanted:
                    trigger.Signal();
                    break;

                case LocalPeerMessageKinds.SyncNow:
                    scheduler.RequestNow();
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "LocalPeerRelayFailed {Kind}", message.Kind);
        }
        finally
        {
            Relaying.Value = false;
        }
    }
}
