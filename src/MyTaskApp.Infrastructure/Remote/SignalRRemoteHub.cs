using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// Como chegar ao hub. Nulo é a rede de verdade; os testes de ponta a ponta
/// trocam pelo servidor em memória (que não fala WebSocket).
/// </summary>
internal sealed record RemoteHubTransport(Func<HttpMessageHandler> Handler, HttpTransportType Transports);

/// <summary>
/// O agente na campainha do servidor (<c>/hubs/remote</c>, seção 4.2): sai
/// daqui (nenhuma porta aberta na máquina), com o token do dispositivo, e
/// religa sozinho com espera crescente de 2 s até 5 min, com variação. Só
/// avisa: o que fazer vem pela fila, assinado (ADR-086).
/// </summary>
internal sealed class SignalRRemoteHub(
    ISyncAuthenticator authenticator,
    ILogger<SignalRRemoteHub> logger,
    RemoteHubTransport? transport = null) : IRemoteHub, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HubConnection? _connection;

    public event Action<Guid>? OperationAvailable;

    public event Action<Guid>? CancelRequested;

    public event Action<Guid>? ApprovalResolved;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (authenticator.ServerBaseUrl is not { } server)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Conectando ou religando: o próprio cliente cuida. Só uma conexão parada é refeita.
            if (_connection is { State: not HubConnectionState.Disconnected })
            {
                return;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            _connection = Build(new Uri(server, RemoteRoutes.Hub.TrimStart('/')));
            await _connection.StartAsync(cancellationToken);
            logger.LogInformation("RemoteHubConnected");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Sem hub (proxy que barra, servidor fora, login vencido), a busca de 30 s segue.
            logger.LogInformation("RemoteHubUnavailable {Reason}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
                logger.LogInformation("RemoteHubDisconnected");
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
        var connection = new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                // O token vai no cabeçalho, renovado pelo mesmo login do sync a cada (re)conexão.
                options.AccessTokenProvider = async () => await authenticator.GetAccessTokenAsync(CancellationToken.None);

                if (transport is not null)
                {
                    options.HttpMessageHandlerFactory = _ => transport.Handler();
                    options.Transports = transport.Transports;
                }
            })
            .WithAutomaticReconnect(new Backoff())
            .AddJsonProtocol(options => RemoteJson.Configure(options.PayloadSerializerOptions))
            .Build();

        connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        connection.On<Guid>(RemoteHubMessages.OperationAvailable, id => OperationAvailable?.Invoke(id));
        connection.On<Guid>(RemoteHubMessages.CancelRequested, id => CancelRequested?.Invoke(id));
        connection.On<Guid, Guid>(RemoteHubMessages.ApprovalResolved, (approvalId, _) => ApprovalResolved?.Invoke(approvalId));

        // Religou: pode ter perdido avisos no meio; uma busca agora acha o que chegou.
        connection.Reconnected += _ =>
        {
            logger.LogInformation("RemoteHubReconnected");
            OperationAvailable?.Invoke(Guid.Empty);
            return Task.CompletedTask;
        };

        return connection;
    }

    /// <summary>2 s, dobrando até 5 min, com até 20% de variação — a conta do <c>SyncScheduler</c>. Nunca desiste.</summary>
    private sealed class Backoff : IRetryPolicy
    {
        private static readonly TimeSpan First = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var exponent = (int)Math.Min(retryContext.PreviousRetryCount, 10);
            var delay = TimeSpan.FromTicks(Math.Min(First.Ticks << exponent, Max.Ticks));
            return delay + (delay * Random.Shared.NextDouble() * 0.2);
        }
    }
}
