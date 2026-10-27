using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Agent.Tests;

/// <summary>
/// O MCP de aprovação com o Claude de verdade (T-26, T-29): o Claude conecta
/// no servidor do agente com o segredo do ambiente, e a ferramenta fora do
/// perfil para na aprovação.
/// </summary>
/// <remarks>
/// Opcional: só com <c>MYTASKAPP_TEST_CLAUDE=1</c> e o Claude instalado. O
/// primeiro não chama o modelo (a conexão com o MCP sai no evento de início,
/// antes do login); o segundo chama (haiku) e precisa do Claude logado.
/// </remarks>
public sealed class ApprovalClaudeTests : IAsyncDisposable
{
    private const string Variable = "MYTASKAPP_TEST_CLAUDE";

    private readonly AgentTestHost _host = AgentTestHost.Create();

    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-approval-").FullName;

    private readonly List<ToolPermissionRequest> _asked = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task TheRealClaude_ConnectsToTheApprovalMcp_WithTheSecretFromTheEnvironment()
    {
        var lines = await RunAsync("oi", AgentToolProfiles.Read, ToolPermissionDecision.Allow());

        var init = lines.Select(line => JsonDocument.Parse(line).RootElement)
            .First(root => root.GetProperty("type").GetString() == "system" && root.GetProperty("subtype").GetString() == "init");
        var server = init.GetProperty("mcp_servers").EnumerateArray().Should().ContainSingle().Subject;
        server.GetProperty("name").GetString().Should().Be("mytaskapp-agent");
        server.GetProperty("status").GetString().Should().Be("connected", "o segredo foi expandido do ambiente e passou as barreiras");
    }

    [Fact]
    public async Task ABashOutsideTheProfile_StopsAtTheApproval_AndDenyingGoesOnWithoutIt()
    {
        var lines = await RunAsync(
            "Rode o comando Bash `echo MYTASKAPP-OK` e diga o que saiu. Se não puder, diga NEGADO.",
            AgentToolProfiles.Edit,
            ToolPermissionDecision.Deny("Negado pelo teste."));
        var provider = _host.Services.GetRequiredService<IHeadlessAgentProvider>();
        var result = lines.SelectMany(provider.ReadEvents).OfType<HeadlessResult>().Single();
        Assert.SkipWhen(result.TerminalReason == "api_error", "O Claude precisa estar logado (claude auth login).");

        _asked.Should().Contain(request => request.ToolName == "Bash" && request.Input.GetProperty("command").GetString()!.Contains("MYTASKAPP-OK"));
        result.DeniedTools.Should().Contain("Bash");
        result.IsError.Should().BeFalse(result.Text);
    }

    private async Task<IReadOnlyList<string>> RunAsync(string prompt, AgentToolProfile profile, ToolPermissionDecision decision)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1", $"Defina {Variable}=1 para rodar o Claude Code de verdade.");

        var provider = _host.Services.GetRequiredService<IHeadlessAgentProvider>();
        var detection = await provider.DetectAsync(Ct);
        Assert.SkipUnless(detection.IsInstalled, "O Claude Code não está instalado neste computador.");

        await using var channel = await _host.Services.GetRequiredService<IHeadlessApprovalGateway>().OpenAsync(
            (request, _) =>
            {
                _asked.Add(request);
                return Task.FromResult(decision);
            },
            Ct);

        var launch = provider.CreateHeadlessLaunch(
            new HeadlessAgentRequest(_folder, Guid.NewGuid(), false, profile, "haiku", null, 4, 0.3m, channel.Access, []),
            detection);
        var lines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        await _host.Services.GetRequiredService<IHeadlessAgentRunner>().RunAsync(
            launch,
            prompt,
            (_, _) => Task.CompletedTask,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            timeout.Token);

        return lines;
    }
}
