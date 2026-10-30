using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Api;

/// <summary>
/// O hub da orquestração (fase 2, T-13; ADR-086): campainha. Cada conexão
/// entra no grupo da máquina e no da conta, e o estado é todo do REST. Os
/// únicos métodos são os do terminal remoto (ADR-099), que só repassam. A
/// autorização é a da API remota, com o dispositivo não revogado
/// (<see cref="ServerSetup.RemoteHubPolicy"/>).
/// </summary>
internal sealed class RemoteHub(RemoteConnections connections, RemoteTerminalUseCases terminals, RemoteTerminalRelay relay) : Hub
{
    [HubMethodName(RemoteHubMethods.TerminalSubscribe)]
    public Task TerminalSubscribe(Guid attachId) =>
        RelayAsync(() => terminals.SubscribeAsync(Caller, Context.ConnectionId, attachId, Context.ConnectionAborted));

    [HubMethodName(RemoteHubMethods.TerminalSend)]
    public Task TerminalSend(Guid attachId, SignedEnvelope input) =>
        RelayAsync(() => terminals.SendAsync(Caller, attachId, input, Context.ConnectionAborted));

    [HubMethodName(RemoteHubMethods.TerminalPublish)]
    public Task TerminalPublish(RemoteTerminalFrame frame) =>
        RelayAsync(() => terminals.PublishAsync(Caller, Context.ConnectionId, frame, Context.ConnectionAborted));

    [HubMethodName(RemoteHubMethods.TerminalClose)]
    public Task TerminalClose(Guid attachId, string? reason) =>
        RelayAsync(() => terminals.CloseAsync(Caller, attachId, reason, Context.ConnectionAborted));

    public override async Task OnConnectedAsync()
    {
        var caller = SyncApiEndpoints.Caller(Context.User!);

        connections.Add(caller, Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, RemoteHubGroups.Device(caller.UserId, caller.DeviceId), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, RemoteHubGroups.Account(caller.UserId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await relay.DisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private SyncCaller Caller => SyncApiEndpoints.Caller(Context.User!);

    /// <summary>A recusa do caso de uso chega ao cliente em palavras; o resto, como erro genérico do hub.</summary>
    private static async Task RelayAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (SyncServerException exception)
        {
            throw new HubException(exception.Message);
        }
    }
}

/// <summary>Uma mensagem do relay para a conexão de quem segue o terminal.</summary>
internal sealed class HubRemoteTerminalTransport(IHubContext<RemoteHub> hub) : IRemoteTerminalTransport
{
    public Task ToConnectionAsync(string connectionId, string message, object?[] args, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendCoreAsync(message, args, cancellationToken);
}

/// <summary>
/// A cota dos métodos do hub, por conexão (ADR-099): o rate limit HTTP só
/// conta a abertura do WebSocket, e cada lote de teclado é uma chamada.
/// Passou de <see cref="RemoteLimits.MaxTerminalMessagesPerSecond"/> num
/// segundo, a chamada é recusada sem chegar ao caso de uso.
/// </summary>
internal sealed class TerminalHubFilter(TimeProvider timeProvider) : IHubFilter
{
    private const string WindowKey = "terminal-window";

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var items = invocationContext.Context.Items;
        var now = timeProvider.GetUtcNow();
        var window = items.TryGetValue(WindowKey, out var value) && value is CallWindow open && now - open.StartedAt < TimeSpan.FromSeconds(1)
            ? open
            : new CallWindow(now);

        items[WindowKey] = window;

        if (++window.Calls > RemoteLimits.MaxTerminalMessagesPerSecond)
        {
            throw new HubException("Mensagens demais para o terminal. Espere um pouco.");
        }

        return next(invocationContext);
    }

    private sealed class CallWindow(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public int Calls { get; set; }
    }
}

/// <summary>Os grupos do hub: um por máquina e um por conta.</summary>
internal static class RemoteHubGroups
{
    public static string Device(Guid userId, Guid deviceId) => $"device:{userId:N}:{deviceId:N}";

    public static string Account(Guid userId) => $"account:{userId:N}";
}

/// <summary>
/// As conexões abertas neste servidor, para revogar derrubar na hora: sem
/// isto, a máquina revogada continuaria ouvindo até a conexão cair sozinha.
/// </summary>
internal sealed class RemoteConnections
{
    private readonly ConcurrentDictionary<string, (SyncCaller Caller, HubCallerContext Context)> _open = new();

    public void Add(SyncCaller caller, HubCallerContext context) => _open[context.ConnectionId] = (caller, context);

    public void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    /// <summary>Fecha as conexões da máquina; devolve quantas eram.</summary>
    public int Drop(Guid userId, Guid deviceId)
    {
        var dropped = 0;

        foreach (var (connectionId, (caller, context)) in _open)
        {
            if (caller.UserId == userId && caller.DeviceId == deviceId && _open.TryRemove(connectionId, out _))
            {
                context.Abort();
                dropped++;
            }
        }

        return dropped;
    }
}

/// <summary>O <see cref="IRemoteNotifier"/> pelo hub. Avisar falhar só vai para o log.</summary>
internal sealed class HubRemoteNotifier(IHubContext<RemoteHub> hub, ILogger<HubRemoteNotifier> logger) : IRemoteNotifier
{
    public Task ToDeviceAsync(Guid userId, Guid deviceId, string message, params object?[] args) =>
        SendAsync(hub.Clients.Group(RemoteHubGroups.Device(userId, deviceId)), message, args);

    public Task ToAccountAsync(Guid userId, string message, params object?[] args) =>
        SendAsync(hub.Clients.Group(RemoteHubGroups.Account(userId)), message, args);

    private async Task SendAsync(IClientProxy clients, string message, object?[] args)
    {
        try
        {
            await clients.SendCoreAsync(message, args);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "RemoteHubNotifyFailed {Message}", message);
        }
    }
}

/// <summary>
/// O WebSocket do navegador não manda cabeçalho, e o SignalR leva o token em
/// <c>?access_token=</c>. Só na rota do hub ele vira o Bearer, e sai da query
/// antes de qualquer outro ver; nas outras rotas, token na query não autentica.
/// </summary>
internal static class HubAccessToken
{
    public const string QueryParameter = "access_token";

    public static Task MoveToHeaderAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;

        if (request.Path.StartsWithSegments(RemoteRoutes.Hub) && request.Query.ContainsKey(QueryParameter))
        {
            var token = request.Query[QueryParameter];

            if (request.Headers.Authorization.Count == 0 && token is [{ Length: > 0 } value])
            {
                request.Headers.Authorization = "Bearer " + value;
            }

            request.QueryString = QueryString.Create(request.Query.Where(pair => pair.Key != QueryParameter));
        }

        return next(context);
    }
}
