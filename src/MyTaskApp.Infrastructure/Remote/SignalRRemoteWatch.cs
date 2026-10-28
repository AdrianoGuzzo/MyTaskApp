using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// Os avisos da conta para as telas de controle (ADR-094), pelo mesmo hub do
/// agente. A conexão fica de pé enquanto houver tela aberta e cai com a
/// última. Só avisa: a tela lê pelo REST, do cursor dela.
/// </summary>
internal sealed class SignalRRemoteWatch(
    ISyncAuthenticator authenticator,
    ILogger<SignalRRemoteWatch> logger,
    RemoteHubTransport? transport = null) : IRemoteWatch, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HubConnection? _connection;

    private int _watchers;

    public event Action<RemoteNotice>? Noticed;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            _watchers++;

            // Conectando ou religando: o próprio cliente cuida. Só uma conexão parada é refeita.
            if (_connection is { State: not HubConnectionState.Disconnected } || authenticator.ServerBaseUrl is not { } server)
            {
                return;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            _connection = Build(new Uri(server, RemoteRoutes.Hub.TrimStart('/')));
            await _connection.StartAsync(cancellationToken);
            logger.LogInformation("RemoteWatchConnected");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Sem hub (proxy que barra, servidor fora, login vencido), as telas leem por tempo.
            logger.LogInformation("RemoteWatchUnavailable {Reason}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            _watchers = Math.Max(0, _watchers - 1);

            if (_watchers == 0 && _connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
                logger.LogInformation("RemoteWatchDisconnected");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }

    private HubConnection Build(Uri hub)
    {
        var connection = RemoteHubConnection.Build(hub, authenticator, transport);
        connection.On<Guid, RemoteOperationStatus>(RemoteHubMessages.OperationChanged, (id, _) => Notify(RemoteNoticeKind.Operation, id));
        connection.On<Guid, long>(RemoteHubMessages.EventsAppended, (id, _) => Notify(RemoteNoticeKind.Events, id));
        connection.On<Guid, Guid>(RemoteHubMessages.ApprovalRequested, (_, operationId) => Notify(RemoteNoticeKind.Approval, operationId));
        connection.On<Guid, Guid>(RemoteHubMessages.ApprovalResolved, (_, operationId) => Notify(RemoteNoticeKind.Approval, operationId));
        connection.On<Guid, long>(RemoteHubMessages.SessionChanged, (id, _) => Notify(RemoteNoticeKind.Session, id));

        // Religou: os avisos do intervalo se perderam; as telas releem do cursor delas.
        connection.Reconnected += _ =>
        {
            logger.LogInformation("RemoteWatchReconnected");
            Notify(RemoteNoticeKind.Reconnected, Guid.Empty);
            return Task.CompletedTask;
        };

        return connection;
    }

    private void Notify(RemoteNoticeKind kind, Guid id)
    {
        try
        {
            Noticed?.Invoke(new RemoteNotice(kind, id));
        }
        catch (Exception exception)
        {
            // Uma tela que falha ao ouvir não derruba a conexão das outras.
            logger.LogWarning(exception, "RemoteWatchHandlerFailed {Kind}", kind);
        }
    }
}
