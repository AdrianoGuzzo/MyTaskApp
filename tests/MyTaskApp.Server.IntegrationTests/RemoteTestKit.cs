using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O que os testes da fila, do hub e das aprovações repetem: o login com o
/// escopo remote, o registro com uma chave nova, o envelope assinado e as
/// chamadas com o JSON estrito da orquestração.
/// </summary>
internal sealed class RemoteTestKit(ApiFactory api) : IDisposable
{
    public const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private readonly List<ECDsa> _keys = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion, [RemoteOperationKinds.EnvironmentInspect], "windows",
        ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, 1, string.Empty);

    public void Dispose() => _keys.ForEach(key => key.Dispose());

    /// <summary>Um dispositivo da conta de <paramref name="subject"/>, registrado com uma chave nova.</summary>
    public async Task<RemoteDevice> DeviceAsync(string name, DeviceRoles roles, string subject = "ana")
    {
        var session = await api.LoginAsync(subject, deviceName: name, scope: WithRemote);
        var key = NewKey();

        await SendAsync<RemoteDeviceInfo>(session, HttpMethod.Put, RemoteRoutes.CurrentDevice,
            new DeviceRegistration(session.DeviceId, roles, DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()), Capabilities));

        return new RemoteDevice(session, key);
    }

    public ECDsa NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        return key;
    }

    /// <summary>Uma inspeção de <paramref name="from"/> para <paramref name="to"/>, que vale por dez minutos.</summary>
    public static RemoteOperationRequest Inspection(Guid workspaceId, RemoteDevice from, RemoteDevice to)
    {
        var now = DateTimeOffset.UtcNow;

        return new RemoteOperationRequest(
            Guid.CreateVersion7(),
            workspaceId,
            to.DeviceId,
            from.DeviceId,
            RemoteOperationKinds.EnvironmentInspect,
            JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
            null,
            [],
            now,
            now.AddMinutes(10));
    }

    public static SignedEnvelope Sign<T>(ECDsa key, T payload, string type, string? kid = null)
    {
        var input = RemoteJws.SigningInput(payload, kid ?? DevicePublicKey.FingerprintOf(key.ExportSubjectPublicKeyInfo()), type);
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return RemoteJws.Compose(input, Base64Url.EncodeToString(signature));
    }

    /// <summary>Cria a operação assinada pelo próprio controlador.</summary>
    public static Task<RemoteOperationView> CreateAsync(RemoteDevice controller, RemoteOperationRequest request) =>
        SendAsync<RemoteOperationView>(controller.Session, HttpMethod.Post, RemoteRoutes.Operations,
            Sign(controller.Key, request, RemoteJws.OperationType), HttpStatusCode.Created);

    public static Task<RemoteOperationView> MoveAsync(RemoteDevice agent, Guid operationId, RemoteOperationStatus status, string? code = null, string? summary = null) =>
        SendAsync<RemoteOperationView>(agent.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(operationId), new OperationStatusUpdate(status, code, summary));

    /// <summary>
    /// Uma conexão ao hub como o cliente de verdade faz. Com o token no
    /// cabeçalho, por WebSocket direto; na query (o WebSocket do navegador),
    /// por long polling, com o negotiate e cada volta levando o token na URL.
    /// </summary>
    public async Task<HubInbox> ListenAsync(DesktopSession session, bool tokenInQuery = false)
    {
        var token = session.Tokens.AccessToken;
        var url = new Uri(api.Server.BaseAddress, tokenInQuery ? $"{RemoteRoutes.Hub}?access_token={Uri.EscapeDataString(token)}" : RemoteRoutes.Hub);

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();

                if (tokenInQuery)
                {
                    options.Transports = HttpTransportType.LongPolling;
                    return;
                }

                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = api.Server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(json => RemoteJson.Configure(json.PayloadSerializerOptions))
            .Build();

        var inbox = new HubInbox(connection);

        try
        {
            await connection.StartAsync(Ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return inbox;
    }

    /// <summary>Mexe no banco direto, para o que o teste não espera acontecer (o prazo, a hora da entrega).</summary>
    public async Task WithDatabaseAsync(Func<ServerDbContext, Task> change)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    public static async Task<T> GetAsync<T>(DesktopSession session, string path)
    {
        using var response = await RequestAsync(session, HttpMethod.Get, path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }

    public static async Task<T> SendAsync<T>(DesktopSession session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await RequestAsync(session, method, path, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }

    public static async Task<(HttpStatusCode Status, string? Code)> ErrorAsync(DesktopSession session, HttpMethod method, string path, object? body)
    {
        using var response = await RequestAsync(session, method, path, body);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct) is { Length: > 0 } text ? text : "{}");
        return (response.StatusCode, problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<HttpResponseMessage> RequestAsync(DesktopSession session, HttpMethod method, string path, object? body)
    {
        using var client = session.Api();
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}

/// <summary>O que chegou pelo hub, na ordem, com os argumentos de cada aviso.</summary>
internal sealed class HubInbox : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Message, object?[] Args)> _received = new();

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly HubConnection _connection;

    public HubInbox(HubConnection connection)
    {
        _connection = connection;

        foreach (var message in new[] { RemoteHubMessages.OperationAvailable, RemoteHubMessages.CancelRequested, RemoteHubMessages.PairingRequested, RemoteHubMessages.PairingApproved })
        {
            connection.On<Guid>(message, id => _received.Enqueue((message, [id])));
        }

        foreach (var message in new[] { RemoteHubMessages.ApprovalRequested, RemoteHubMessages.ApprovalResolved })
        {
            connection.On<Guid, Guid>(message, (approvalId, operationId) => _received.Enqueue((message, [approvalId, operationId])));
        }

        connection.On<Guid, RemoteOperationStatus>(RemoteHubMessages.OperationChanged, (id, status) => _received.Enqueue((RemoteHubMessages.OperationChanged, [id, status])));
        connection.On<Guid, long>(RemoteHubMessages.EventsAppended, (id, seq) => _received.Enqueue((RemoteHubMessages.EventsAppended, [id, seq])));
        connection.On<Guid, long>(RemoteHubMessages.SessionChanged, (id, version) => _received.Enqueue((RemoteHubMessages.SessionChanged, [id, version])));
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public IReadOnlyList<(string Message, object?[] Args)> Received => [.. _received];

    /// <summary>A conexão caiu (o servidor fechou).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Espera um aviso com estes argumentos; o hub entrega fora de ordem com o REST.</summary>
    public async Task ExpectAsync(string message, params object?[] args)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!_received.Any(item => item.Message == message && item.Args.SequenceEqual(args)))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Não chegou {message}({string.Join(", ", args)}). Chegou: {string.Join("; ", _received.Select(item => $"{item.Message}({string.Join(", ", item.Args)})"))}");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>Um dispositivo registrado na orquestração, com a chave dele.</summary>
internal sealed record RemoteDevice(DesktopSession Session, ECDsa Key)
{
    public Guid DeviceId => Session.DeviceId;
}
