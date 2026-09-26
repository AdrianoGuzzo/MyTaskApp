using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Agents;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// A porta local dos hooks (ADR-037), com HTTP de verdade em 127.0.0.1: o que
/// ela aceita, o que recusa antes de ler, e a ordem de entrega.
/// </summary>
public sealed class AgentEventListenerTests : IAsyncDisposable
{
    private static readonly Guid SessionId = Guid.Parse("0199a0c0-0000-7000-8000-000000000001");
    private static readonly Guid TaskId = Guid.Parse("0199a0c0-0000-7000-8000-000000000002");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<RecordAgentEvent> _delivered = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly AgentEventListener _listener;

    public AgentEventListenerTests()
    {
        _listener = new AgentEventListener(Deliver, preferredPort: 0, TimeProvider.System, NullLogger<AgentEventListener>.Instance);
        _listener.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _listener.DisposeAsync();
    }

    private Task Deliver(RecordAgentEvent command, CancellationToken cancellationToken)
    {
        lock (_delivered)
        {
            _delivered.Add(command);
        }

        _arrived.Release();
        return Task.CompletedTask;
    }

    private async Task WaitForAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            (await _arrived.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().BeTrue("o aviso deveria ter sido entregue");
        }
    }

    private HttpRequestMessage Hook(string json, string? token = "segredo", string? session = null, Uri? uri = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri ?? _listener.Address)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Add("X-MyTaskApp-Session", session ?? SessionId.ToString());
        request.Headers.Add("X-MyTaskApp-Task", TaskId.ToString());
        return request;
    }

    [Fact]
    public void ItListensOnlyOnLoopback_AtTheEventsPath()
    {
        var address = _listener.Address!;

        address.Host.Should().Be("127.0.0.1");
        address.AbsolutePath.Should().Be("/api/claude/events");
        address.Port.Should().BePositive();
    }

    [Fact]
    public async Task AHook_IsAnsweredWithAnEmptyDecision_AndDelivered()
    {
        using var response = await _http.SendAsync(
            Hook("""{"hook_event_name":"Stop","session_id":"abc","cwd":"C:\\wt"}"""), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("{}");

        await WaitForAsync(1);
        var command = _delivered.Should().ContainSingle().Subject;
        command.Token.Should().Be("segredo");
        command.Event.Type.Should().Be(AgentEventType.ResponseCompleted);
        command.Event.AgentSessionId.Should().Be(SessionId);
        command.Event.TaskId.Should().Be(TaskId);
        command.Event.ExternalSessionId.Should().Be("abc");
    }

    /// <summary>"Pergunta" e "voltou a trabalhar" não podem trocar de lugar.</summary>
    [Fact]
    public async Task Events_AreDeliveredInTheOrderTheyArrived()
    {
        string[] names = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop"];

        foreach (var name in names)
        {
            using var response = await _http.SendAsync(Hook($$"""{"hook_event_name":"{{name}}","tool_name":"ExitPlanMode"}"""), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await WaitForAsync(names.Length);
        _delivered.Select(command => command.Event.SourceEvent).Should().Equal(names);
    }

    /// <summary>Variável não resolvida vira cabeçalho vazio: sem segredo, nada entra.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("segredo", "")]
    [InlineData("segredo", "not-a-guid")]
    public async Task WithoutSecretOrSession_ItIsUnauthorized(string? token, string? session)
    {
        var request = Hook("""{"hook_event_name":"Stop"}""", token: null, session: session ?? SessionId.ToString());

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        using var response = await _http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _delivered.Should().BeEmpty();
    }

    /// <summary>Um site aberto no navegador não fala com a porta.</summary>
    [Fact]
    public async Task ABrowserRequest_IsForbidden()
    {
        var request = Hook("""{"hook_event_name":"Stop"}""");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await _http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _delivered.Should().BeEmpty();
    }

    /// <summary>DNS rebinding: outro nome apontando para 127.0.0.1 não é o hook.</summary>
    [Fact]
    public async Task AnotherHostName_IsForbidden()
    {
        var request = Hook("""{"hook_event_name":"Stop"}""");
        request.Headers.Host = $"evil.example:{_listener.Address!.Port}";

        using var response = await _http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LocalhostByName_IsAccepted()
    {
        var uri = new UriBuilder(_listener.Address!) { Host = "localhost" }.Uri;
        var request = Hook("""{"hook_event_name":"Stop"}""", uri: _listener.Address);
        request.Headers.Host = $"localhost:{uri.Port}";

        using var response = await _http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("{ não é json")]
    [InlineData("""{"sem":"evento"}""")]
    public async Task WhatIsNotAHook_IsABadRequest(string body)
    {
        using var response = await _http.SendAsync(Hook(body), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherPath_IsNotFound_AndGetIsNotAllowed()
    {
        using var other = await _http.SendAsync(
            Hook("{}", uri: new Uri(_listener.Address!, "/api/outra")), Ct);
        using var get = await _http.GetAsync(_listener.Address, Ct);

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>O tamanho é conferido antes de ler: nada de 1 GB na memória.</summary>
    [Fact]
    public async Task ATooLargeBody_IsRefused_BeforeBeingRead()
    {
        var status = await RawAsync(
            $"POST /api/claude/events HTTP/1.1\r\nHost: 127.0.0.1:{_listener.Address!.Port}\r\n"
            + $"Content-Length: {AgentEventListener.MaxBodyBytes + 1}\r\n\r\n");

        status.Should().Be("HTTP/1.1 413 RequestEntityTooLarge");
    }

    [Fact]
    public async Task APostWithoutLength_IsRefused()
    {
        var status = await RawAsync(
            $"POST /api/claude/events HTTP/1.1\r\nHost: 127.0.0.1:{_listener.Address!.Port}\r\n\r\n");

        status.Should().Be("HTTP/1.1 411 LengthRequired");
    }

    private async Task<string?> RawAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _listener.Address!.Port, Ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);

        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync(Ct);
    }

    /// <summary>Corpo em pedaços (<c>Transfer-Encoding: chunked</c>) também vale.</summary>
    [Fact]
    public async Task AChunkedBody_IsRead()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _listener.Address!.Port, Ct);
        var stream = client.GetStream();

        var body = """{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Permitir?"}""";
        var first = body[..20];
        var second = body[20..];
        var request =
            $"POST /api/claude/events HTTP/1.1\r\nHost: 127.0.0.1:{_listener.Address.Port}\r\n"
            + $"Authorization: Bearer segredo\r\nX-MyTaskApp-Session: {SessionId}\r\n"
            + "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"
            + $"{first.Length:x}\r\n{first}\r\n{second.Length:x}\r\n{second}\r\n0\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);

        using var reader = new StreamReader(stream);
        (await reader.ReadLineAsync(Ct)).Should().Be("HTTP/1.1 200 OK");

        await WaitForAsync(1);
        _delivered.Single().Event.Type.Should().Be(AgentEventType.NeedsUserInput);
        _delivered.Single().Event.Message.Should().Be("Permitir?");
    }

    /// <summary>Porta preferida ocupada por outro programa: vale qualquer livre.</summary>
    [Fact]
    public async Task WithThePreferredPortTaken_ItListensOnAnother()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        await using var listener = new AgentEventListener(
            Deliver, taken, TimeProvider.System, NullLogger<AgentEventListener>.Instance);
        listener.Start();

        listener.Address.Should().NotBeNull();
        listener.Address!.Port.Should().NotBe(taken);
    }

    [Fact]
    public async Task AfterDisposal_ThePortIsClosed()
    {
        var address = _listener.Address!;

        await _listener.DisposeAsync();

        var connect = async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, address.Port, Ct);
        };

        await connect.Should().ThrowAsync<SocketException>();
    }
}
