using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MyTaskApp.Agent.Approvals;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Agent.Tests;

/// <summary>
/// O MCP de aprovação do agente (ADR-089), com um cliente MCP de verdade no
/// lugar do Claude: o contrato da <c>--permission-prompt-tool</c> e as
/// barreiras antes dele.
/// </summary>
public sealed class ApprovalMcpServerTests : IAsyncDisposable
{
    private readonly ApprovalMcpServer _server = new(NullLoggerFactory.Instance);

    private readonly List<ToolPermissionRequest> _asked = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private Task<IHeadlessApprovalChannel> OpenAsync(ToolPermissionDecision decision) =>
        _server.OpenAsync(
            (request, _) =>
            {
                _asked.Add(request);
                return Task.FromResult(decision);
            },
            Ct);

    private static async Task<McpClient> ConnectAsync(HeadlessApprovalAccess access) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = access.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access.Token}" },
            }),
            cancellationToken: Ct);

    private static Dictionary<string, object?> Call(string tool, object input) =>
        new() { ["tool_name"] = tool, ["input"] = input, ["tool_use_id"] = "toolu_09" };

    private static JsonElement Answer(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    [Fact]
    public async Task Allowing_GivesTheInputBackAsItCame_AndThePersonSawTheToolAndTheInput()
    {
        await using var channel = await OpenAsync(ToolPermissionDecision.Allow());
        await using var client = await ConnectAsync(channel.Access);

        (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).Should().Equal(ApprovalMcpServer.ToolName);
        var result = await client.CallToolAsync(ApprovalMcpServer.ToolName, Call("Bash", new { command = "dotnet test", timeout = 600 }), cancellationToken: Ct);

        var answer = Answer(result);
        answer.GetProperty("behavior").GetString().Should().Be("allow");
        answer.GetProperty("updatedInput").GetProperty("command").GetString().Should().Be("dotnet test");
        answer.GetProperty("updatedInput").GetProperty("timeout").GetInt32().Should().Be(600);
        var asked = _asked.Should().ContainSingle().Subject;
        asked.ToolName.Should().Be("Bash");
        asked.ToolUseId.Should().Be("toolu_09");
        asked.Input.GetProperty("command").GetString().Should().Be("dotnet test");
    }

    [Fact]
    public async Task Denying_TellsTheModelWhy()
    {
        await using var channel = await OpenAsync(ToolPermissionDecision.Deny("Negado no celular."));
        await using var client = await ConnectAsync(channel.Access);

        var answer = Answer(await client.CallToolAsync(ApprovalMcpServer.ToolName, Call("Write", new { file_path = "a.txt" }), cancellationToken: Ct));

        answer.GetProperty("behavior").GetString().Should().Be("deny");
        answer.GetProperty("message").GetString().Should().Be("Negado no celular.");
        answer.TryGetProperty("updatedInput", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WhoDecidesFailing_IsADenial()
    {
        await using var channel = await _server.OpenAsync((_, _) => throw new InvalidOperationException("o servidor caiu"), Ct);
        await using var client = await ConnectAsync(channel.Access);

        var answer = Answer(await client.CallToolAsync(ApprovalMcpServer.ToolName, Call("Bash", new { command = "ls" }), cancellationToken: Ct));

        answer.GetProperty("behavior").GetString().Should().Be("deny");
    }

    [Fact]
    public async Task EachSession_HasItsOwnSecret_AndAClosedOne_IsRefused()
    {
        var first = await OpenAsync(ToolPermissionDecision.Allow());
        await using var second = await OpenAsync(ToolPermissionDecision.Allow());

        first.Access.Token.Should().NotBe(second.Access.Token).And.HaveLength(64);
        first.Access.Endpoint.Should().Be(second.Access.Endpoint, "um servidor só, uma porta");
        first.Access.Endpoint.Host.Should().Be("127.0.0.1");

        (await PostAsync(first.Access, first.Access.Token)).Should().NotBe(HttpStatusCode.Unauthorized);
        await first.DisposeAsync();
        (await PostAsync(first.Access, first.Access.Token)).Should().Be(HttpStatusCode.Unauthorized, "fechado o canal, o segredo morre");
        (await PostAsync(second.Access, second.Access.Token)).Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithoutTheSecret_WithAnOrigin_OrAnotherHost_NothingReachesTheTool()
    {
        await using var channel = await OpenAsync(ToolPermissionDecision.Allow());
        var access = channel.Access;

        (await PostAsync(access, null)).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(access, new string('0', 64))).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(access, access.Token, origin: "http://evil.example")).Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(access, access.Token, host: "rebind.example")).Should().Be(HttpStatusCode.BadRequest);
        _asked.Should().BeEmpty();
    }

    /// <summary>Um <c>tools/call</c> cru, para ver só a resposta das barreiras.</summary>
    private static async Task<HttpStatusCode> PostAsync(HeadlessApprovalAccess access, string? token, string? origin = null, string? host = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
