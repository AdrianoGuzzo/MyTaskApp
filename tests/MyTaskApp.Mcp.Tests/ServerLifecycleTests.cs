using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Domain;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tests;

/// <summary>Subir, parar, porta e as barreiras da porta de entrada (ADR-059).</summary>
public class ServerLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"teste","version":"1"}}}""";

    [Fact]
    public async Task Starting_ListensOnLoopback_AtTheConfiguredPort()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        host.Manager.Status.State.Should().Be(McpServerState.Running);
        host.Manager.Status.Endpoint.Should().Be(new Uri($"http://127.0.0.1:{host.Port}/mcp"));

        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        client.ServerInfo.Name.Should().Be("mytaskapp");
    }

    [Fact]
    public async Task ListingTools_ShowsTheCatalog_EachWithAnInputSchema()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        tools.Select(tool => tool.Name).Should().Contain(
        [
            "task_list", "task_get", "task_create", "task_update", "time_tracking_start", "time_entry_create",
            "time_entry_get_summary", "database_profile_get", "anonymization_profile_update", "tag_list",
        ]);
        tools.Should().HaveCount(host.Manager.Tools.Count);
        tools.Should().OnlyContain(tool => tool.ProtocolTool.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task ResourcesAndPrompts_AreListed()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.ListResourcesAsync(cancellationToken: Ct)).Select(resource => resource.Uri)
            .Should().Contain(["mytaskapp://today", "mytaskapp://timer", "mytaskapp://databases/connections"]);

        (await client.ListPromptsAsync(cancellationToken: Ct)).Select(prompt => prompt.Name)
            .Should().Contain(["review_today", "weekly_summary", "audit_time_entries", "review_anonymization_profile"]);

        var today = await client.ReadResourceAsync("mytaskapp://today", cancellationToken: Ct);
        today.Contents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Stopping_ReleasesThePort()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        var stopped = await host.Manager.StopAsync(Ct);

        stopped.State.Should().Be(McpServerState.Stopped);
        stopped.Endpoint.Should().BeNull();

        // A porta está livre de novo: outro programa consegue escutar nela.
        var listener = new TcpListener(IPAddress.Loopback, host.Port) { ExclusiveAddressUse = true };
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task Restarting_ComesBackOnTheSamePort()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        await host.Manager.StopAsync(Ct);
        var again = await host.Manager.StartAsync(Ct);

        again.State.Should().Be(McpServerState.Running);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        (await client.ListToolsAsync(cancellationToken: Ct)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChangingThePort_WhileRunning_MovesTheServer()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var newPort = McpTestHost.FreePort();

        var settings = await host.SaveSettingsAsync(enabled: true, newPort, cancellationToken: Ct);
        var status = await host.Manager.ApplySettingsAsync(settings, Ct);

        status.State.Should().Be(McpServerState.Running);
        status.Port.Should().Be(newPort);
        status.Endpoint.Should().Be(new Uri($"http://127.0.0.1:{newPort}/mcp"));
    }

    [Fact]
    public async Task APortInUse_EndsInError_WithAReasonToShow()
    {
        await using var host = await McpTestHost.CreateAsync(start: false, cancellationToken: Ct);

        var taken = new TcpListener(IPAddress.Loopback, host.Port) { ExclusiveAddressUse = true };
        taken.Start();

        try
        {
            var status = await host.Manager.StartAsync(Ct);

            status.State.Should().Be(McpServerState.Error);
            status.Error.Should().Contain($"porta {host.Port}").And.Contain("em uso");

            (await host.Manager.CheckPortAsync(host.Port, Ct)).IsAvailable.Should().BeFalse();
        }
        finally
        {
            taken.Stop();
        }

        // Liberada a porta, o mesmo gerente sobe.
        (await host.Manager.StartAsync(Ct)).State.Should().Be(McpServerState.Running);
    }

    [Fact]
    public async Task Disabled_RefusesToStart()
    {
        await using var host = await McpTestHost.CreateAsync(start: false, cancellationToken: Ct);
        await host.SaveSettingsAsync(enabled: false, host.Port, cancellationToken: Ct);

        await FluentActions.Awaiting(() => host.Manager.StartAsync(Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*Habilite*");

        host.Manager.Status.State.Should().Be(McpServerState.Stopped);
    }

    [Fact]
    public async Task ConcurrentStarts_StartOnce()
    {
        await using var host = await McpTestHost.CreateAsync(start: false, cancellationToken: Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => host.Manager.StartAsync(Ct)));

        results.Should().OnlyContain(status => status.State == McpServerState.Running);
        results.Select(status => status.Port).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task StartWithApp_StartsOnlyWhenEnabledAndAsked()
    {
        await using var host = await McpTestHost.CreateAsync(start: false, cancellationToken: Ct);

        (await host.Manager.StartIfConfiguredAsync(Ct)).State.Should().Be(McpServerState.Stopped);

        await host.Runner.RunAsync<UpdateMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new UpdateMcpServerSettings(true, host.Port, StartWithApp: true, ReadOnly: false), token),
            Ct);

        (await host.Manager.StartIfConfiguredAsync(Ct)).State.Should().Be(McpServerState.Running);
    }

    [Fact]
    public async Task Settings_SurviveARestartOfTheApp()
    {
        await using var host = await McpTestHost.CreateAsync(start: false, cancellationToken: Ct);

        await host.Runner.RunAsync<UpdateMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new UpdateMcpServerSettings(true, 6123, StartWithApp: true, ReadOnly: true), token),
            Ct);

        var read = await host.Runner.RunAsync<GetMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new GetMcpServerSettings(), token), Ct);

        read.Should().BeEquivalentTo(new McpServerSettings(true, 6123, true, true));
    }

    [Fact]
    public async Task WithoutToken_IsUnauthorized()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        using var http = new HttpClient();
        using var response = await http.SendAsync(Post(host.Endpoint, token: null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithAWrongToken_IsUnauthorized()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        await FluentActions.Awaiting(() => host.ConnectAsync("token-errado", Ct)).Should().ThrowAsync<Exception>();

        using var http = new HttpClient();
        using var response = await http.SendAsync(Post(host.Endpoint, "token-errado"), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegeneratingTheToken_LocksTheOldOneOut()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var old = await host.Manager.GetAccessTokenAsync(Ct);

        var fresh = await host.Manager.RegenerateAccessTokenAsync(Ct);

        fresh.Should().NotBe(old);
        using var http = new HttpClient();
        (await http.SendAsync(Post(host.Endpoint, old), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await http.SendAsync(Post(host.Endpoint, fresh), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AForeignHost_IsRejected_AgainstDnsRebinding()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var token = await host.Manager.GetAccessTokenAsync(Ct);

        using var http = new HttpClient();
        using var request = Post(host.Endpoint, token);
        request.Headers.Host = $"evil.example.com:{host.Port}";

        (await http.SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ABrowserOrigin_IsRejected_EvenWithTheToken()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var token = await host.Manager.GetAccessTokenAsync(Ct);

        using var http = new HttpClient();
        using var request = Post(host.Endpoint, token);
        request.Headers.Add("Origin", "https://evil.example.com");

        using var response = await http.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task DeniedRequests_ShowUpInTheActivityLog_WithoutTheToken()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        using var http = new HttpClient();
        await http.SendAsync(Post(host.Endpoint, "nao-e-o-token"), Ct);

        host.Manager.Activity.Snapshot().Should().Contain(entry => entry.Outcome == McpActivityOutcome.Denied);
        host.Logs.All.Should().NotContain("nao-e-o-token");
        host.Logs.All.Should().NotContain(await host.Manager.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task ThePort_IsExclusive_EvenAgainstAddressReuse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "SO_EXCLUSIVEADDRUSE é do Windows.");

        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        using var intruder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        intruder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        FluentActions.Invoking(() => intruder.Bind(new IPEndPoint(IPAddress.Loopback, host.Port)))
            .Should().Throw<SocketException>();
    }

    [Fact]
    public async Task AFloodOfDeniedRequests_IsRecordedOnce_WithTheCount()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);

        using var http = new HttpClient();

        for (var i = 0; i < 30; i++)
        {
            using var response = await http.SendAsync(Post(host.Endpoint, "errado"), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        host.Manager.Activity.Snapshot().Count(entry => entry.Outcome == McpActivityOutcome.Denied)
            .Should().BeLessThan(5, "um registro por segundo, no máximo");
    }

    [Fact]
    public async Task Disposing_StopsTheServer()
    {
        var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var port = host.Port;

        await host.DisposeAsync();

        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start();
        listener.Stop();
    }

    private static HttpRequestMessage Post(Uri endpoint, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
