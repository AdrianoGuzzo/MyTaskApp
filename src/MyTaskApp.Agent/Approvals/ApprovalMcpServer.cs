using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Agent.Approvals;

/// <summary>
/// O MCP de aprovação do agente (ADR-089, item 5): a ferramenta que o Claude
/// sem terminal chama, pela <c>--permission-prompt-tool</c>, antes de usar o
/// que o perfil não libera. A pergunta vira um pedido de aprovação para a
/// pessoa do outro lado, e a resposta volta ao Claude.
/// </summary>
/// <remarks>
/// <para>
/// O mesmo padrão do <c>McpSecurityMiddleware</c> do desktop, antes de o SDK
/// ver a requisição: só loopback (o servidor nem escuta fora dele), <c>Host</c>
/// <c>127.0.0.1</c> na porta certa, nenhum <c>Origin</c>, e o segredo de uma
/// sessão aberta no <c>Authorization: Bearer</c>. O segredo nasce aqui, vai
/// para o ambiente do processo do Claude e morre quando a sessão fecha o
/// canal; daqui só o hash fica guardado.
/// </para>
/// <para>
/// O contrato da ferramenta é o da <c>--permission-prompt-tool</c>: entra
/// <c>{tool_name, input, tool_use_id}</c>; sai um texto JSON
/// <c>{"behavior":"allow","updatedInput":…}</c> ou
/// <c>{"behavior":"deny","message":…}</c>. A entrada volta como veio: daqui
/// nada é reescrito.
/// </para>
/// <para>
/// Sobe na primeira sessão, numa porta livre, e fica no ar até o agente
/// parar. Qualquer falha ao decidir é negação.
/// </para>
/// </remarks>
internal sealed class ApprovalMcpServer(ILoggerFactory loggerFactory) : IHeadlessApprovalGateway, IAsyncDisposable
{
    public const string ServerName = "mytaskapp-agent";

    public const string ToolName = "approval";

    public const string TokenVariable = "MYTASKAPP_AGENT_APPROVAL_TOKEN";

    /// <summary>Quantas sessões em paralelo, no máximo: um processo qualquer da máquina não abre milhares de conexões.</summary>
    private const int MaxConnections = 32;

    private const long MaxRequestBodyBytes = 1024 * 1024;

    private const string SessionClaim = "mytaskapp-agent-session";

    private readonly ConcurrentDictionary<string, ToolPermissionDecider> _sessions = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _starting = new(1, 1);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ApprovalMcpServer>();

    private WebApplication? _app;

    private Uri? _endpoint;

    public async Task<IHeadlessApprovalChannel> OpenAsync(ToolPermissionDecider decide, CancellationToken cancellationToken)
    {
        var endpoint = await EnsureStartedAsync(cancellationToken);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var key = Hash(token);
        _sessions[key] = decide;

        return new Channel(
            new HeadlessApprovalAccess(endpoint, ServerName, ToolName, TokenVariable, token),
            () => _sessions.TryRemove(key, out _));
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.Clear();

        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }

        _starting.Dispose();
    }

    private async Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is { } ready)
        {
            return ready;
        }

        await _starting.WaitAsync(cancellationToken);

        try
        {
            if (_endpoint is { } started)
            {
                return started;
            }

            var port = FreePort();
            var app = Build(port);
            await app.StartAsync(cancellationToken);
            _app = app;
            _endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            _logger.LogInformation("ApprovalMcpStarted {Port}", port);
            return _endpoint;
        }
        finally
        {
            _starting.Release();
        }
    }

    private WebApplication Build(int port)
    {
        // Vazio de propósito: sem appsettings, sem ASPNETCORE_*, sem URL de fora.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApprovalMcpServer).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });

        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = MaxConnections;
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });

        builder.WebHost.UseSockets(sockets => sockets.CreateBoundListenSocket = endpoint =>
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(endpoint);
            return socket;
        });

        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddLogging();
        builder.Services.AddRoutingCore();
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = ServerName, Version = "1" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [Tool] }))
            .WithCallToolHandler(CallAsync);

        var app = builder.Build();
        app.Use(GuardAsync);
        app.MapMcp("/mcp");
        return app;
    }

    private static readonly Tool Tool = new()
    {
        Name = ToolName,
        Description = "Pergunta à pessoa se o agente pode usar uma ferramenta que o perfil não libera.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool_name"] = new JsonObject { ["type"] = "string" },
                ["input"] = new JsonObject { ["type"] = "object" },
                ["tool_use_id"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("tool_name", "input"),
        }),
    };

    /// <summary>As barreiras, nesta ordem; quem passa leva a sessão no <see cref="HttpContext.User"/>.</summary>
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        if (Refusal(context) is { } refusal)
        {
            _logger.LogWarning("ApprovalMcpDenied {Reason}", refusal.Reason);
            context.Response.StatusCode = refusal.Status;
            await context.Response.WriteAsync(refusal.Status == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden", context.RequestAborted);
            return;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionClaim, Hash(Bearer(context)!))], "approval"));
        await next(context);
    }

    private (int Status, string Reason)? Refusal(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            return (StatusCodes.Status403Forbidden, "Conexão de fora do loopback.");
        }

        var host = context.Request.Host;

        if (!host.HasValue
            || host.Port != context.Connection.LocalPort
            || !(host.Host == "127.0.0.1" || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return (StatusCodes.Status400BadRequest, "Host não aceito.");
        }

        if (context.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            return (StatusCodes.Status403Forbidden, "Requisição com Origin recusada.");
        }

        return Bearer(context) is { } token && _sessions.ContainsKey(Hash(token))
            ? null
            : (StatusCodes.Status401Unauthorized, "Sem o segredo de uma sessão aberta.");
    }

    private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var decision = await DecideAsync(request, cancellationToken);

        var answer = decision.IsAllowed
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(Argument(request, "input")?.GetRawText() ?? "{}") }
            : new JsonObject { ["behavior"] = "deny", ["message"] = decision.Message ?? "Negado." };

        return new CallToolResult { Content = [new TextContentBlock { Text = answer.ToJsonString() }] };
    }

    private async Task<ToolPermissionDecision> DecideAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        if (request.Params?.Name != ToolName)
        {
            return ToolPermissionDecision.Deny("Ferramenta desconhecida.");
        }

        // A sessão pode ter fechado entre a barreira e aqui: sem quem decida, nega.
        if (request.User?.FindFirst(SessionClaim)?.Value is not { } key || !_sessions.TryGetValue(key, out var decide))
        {
            return ToolPermissionDecision.Deny("A sessão já terminou.");
        }

        if (Argument(request, "tool_name") is not { ValueKind: JsonValueKind.String } name || string.IsNullOrWhiteSpace(name.GetString()))
        {
            return ToolPermissionDecision.Deny("Pedido sem o nome da ferramenta.");
        }

        var input = Argument(request, "input") is { ValueKind: JsonValueKind.Object } given ? given.Clone() : JsonSerializer.SerializeToElement(new JsonObject());
        var id = Argument(request, "tool_use_id") is { ValueKind: JsonValueKind.String } use ? use.GetString() : null;

        try
        {
            return await decide(new ToolPermissionRequest(name.GetString()!, input, id), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "ApprovalDecisionFailed {Tool}", name.GetString());
            return ToolPermissionDecision.Deny("Não foi possível pedir a aprovação. Negado.");
        }
    }

    private static JsonElement? Argument(RequestContext<CallToolRequestParams> request, string name) =>
        request.Params?.Arguments is { } arguments && arguments.TryGetValue(name, out var value) ? value : null;

    private static string? Bearer(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization["Bearer ".Length..].Trim() is { Length: > 0 } token
            ? token
            : null;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Channel(HeadlessApprovalAccess access, Action close) : IHeadlessApprovalChannel
    {
        public HeadlessApprovalAccess Access { get; } = access;

        public ValueTask DisposeAsync()
        {
            close();
            return ValueTask.CompletedTask;
        }
    }
}
