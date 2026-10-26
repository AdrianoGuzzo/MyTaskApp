using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O Claude sem terminal com o Claude de verdade (T-26, ADR-089): o que os
/// testes com saídas gravadas supõem do <c>stream-json</c> vale na versão
/// instalada, e a linha de comando que o app gera é aceita.
/// </summary>
/// <remarks>
/// Opcional: só com <c>MYTASKAPP_TEST_CLAUDE=1</c>, o Claude instalado e com
/// login. Chama o modelo (haiku), então custa alguns centavos. Com
/// <c>MYTASKAPP_TEST_CLAUDE_RECORD=&lt;pasta&gt;</c>, grava a saída que viu (sem
/// os caminhos da máquina), para trocar as <c>*.documented.jsonl</c>. O
/// contrato da <c>--permission-prompt-tool</c> é conferido com o MCP de
/// aprovação, nos testes do agente.
/// </remarks>
public sealed class ClaudeHeadlessTests : IDisposable
{
    public const string Variable = "MYTASKAPP_TEST_CLAUDE";

    public const string RecordVariable = "MYTASKAPP_TEST_CLAUDE_RECORD";

    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-claude-").FullName;

    private readonly string _state = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);

        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    [Fact]
    public async Task AReadQuestion_EndsWithoutApproval_AndTheEventsAreTheOnesTheReaderKnows()
    {
        var (provider, detection) = await InstalledAsync();
        await File.WriteAllTextAsync(Path.Combine(_folder, "LEIA.md"), "Palavra de controle: ABACAXI-42", Ct);

        var (events, lines) = await RunAsync(provider, detection, Profile(["Read", "Grep", "Glob"]), "Leia o arquivo LEIA.md e responda só a palavra de controle.");
        Record("read-and-answer", lines);

        events.OfType<HeadlessSessionInitialized>().Should().ContainSingle().Which.Tools.Should().BeEquivalentTo(["Read", "Grep", "Glob"]);
        events.OfType<HeadlessToolUse>().Should().Contain(use => use.Name == "Read" && use.Summary.EndsWith("LEIA.md", StringComparison.OrdinalIgnoreCase));
        events.OfType<HeadlessToolResult>().Should().NotBeEmpty();
        var result = events.OfType<HeadlessResult>().Should().ContainSingle().Subject;
        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("ABACAXI-42");
        result.Turns.Should().BePositive();
        result.CostUsd.Should().BePositive();
    }

    /// <summary>Sem canal de aprovação (<c>--permission-prompts none</c>), o Bash fora do perfil é negado, e o fim diz qual.</summary>
    [Fact]
    public async Task ABashOutsideTheProfile_WithoutAnApprovalChannel_IsDenied()
    {
        var (provider, detection) = await InstalledAsync();

        var (events, lines) = await RunAsync(
            provider,
            detection,
            Profile(["Read", "Bash"], allowed: ["Read"]),
            "Rode o comando Bash `echo MYTASKAPP-OK` e diga o que saiu. Se não puder, diga NEGADO.");
        Record("tool-denied", lines);

        events.OfType<HeadlessToolUse>().Should().Contain(use => use.Name == "Bash");
        events.OfType<HeadlessToolResult>().Should().Contain(result => result.IsError);
        events.OfType<HeadlessResult>().Should().ContainSingle().Which.DeniedTools.Should().Contain("Bash");
    }

    private async Task<(ClaudeCodeCliProvider Provider, CliDetectionResult Detection)> InstalledAsync()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"Defina {Variable}=1 para rodar o Claude Code de verdade (chama o modelo).");

        var provider = new ClaudeCodeCliProvider(
            ExecutableLocator.ForCurrentSystem(),
            new ProcessRunner(),
            new ClaudeCodeHooks(_state, Path.Combine(_state, "home"), [], NullLogger<ClaudeCodeHooks>.Instance),
            NullLogger<ClaudeCodeCliProvider>.Instance);
        var detection = await provider.DetectAsync(Ct);
        Assert.SkipUnless(detection.IsInstalled, "O Claude Code não está instalado neste computador.");
        provider.HeadlessUnavailableReason(detection).Should().BeNull();

        return (provider, detection);
    }

    private async Task<(IReadOnlyList<HeadlessAgentEvent> Events, IReadOnlyList<string> Lines)> RunAsync(
        ClaudeCodeCliProvider provider,
        CliDetectionResult detection,
        AgentToolProfile profile,
        string prompt)
    {
        var launch = provider.CreateHeadlessLaunch(
            new HeadlessAgentRequest(_folder, Guid.NewGuid(), false, profile, "haiku", null, 6, 0.5m, null, [_state]),
            detection);
        var lines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        var result = await new HeadlessAgentRunner(NullLogger<HeadlessAgentRunner>.Instance).RunAsync(
            launch,
            prompt,
            (_, _) => Task.CompletedTask,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            timeout.Token);

        var events = lines.SelectMany(provider.ReadEvents).ToList();
        events.OfType<HeadlessResult>().Should().ContainSingle($"o fim sempre vem (exit {result.ExitCode}: {result.StandardError})");
        events.OfType<HeadlessResult>().Single().TerminalReason.Should().NotBe("api_error", "o Claude precisa estar logado: claude auth login");

        return (events, lines);
    }

    private static AgentToolProfile Profile(IReadOnlyList<string> tools, IReadOnlyList<string>? allowed = null) =>
        new("teste", "Teste", "Só para o teste.", tools, allowed ?? tools, []);

    /// <summary>A saída vista, sem os caminhos da máquina nem as listas de skills e plugins de quem rodou.</summary>
    private void Record(string name, IReadOnlyList<string> lines)
    {
        if (Environment.GetEnvironmentVariable(RecordVariable) is not { Length: > 0 } folder)
        {
            return;
        }

        var clean = lines.Select(line =>
        {
            if (JsonNode.Parse(line) is not JsonObject node)
            {
                return line;
            }

            foreach (var list in new[] { "slash_commands", "skills", "plugins", "agents", "terminal_slash_commands", "additional_directories" })
            {
                if (node[list] is JsonArray)
                {
                    node[list] = new JsonArray();
                }
            }

            return node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                .Replace(JsonEncodedText.Encode(_folder).ToString(), "/work/eco-feature-1", StringComparison.Ordinal)
                .Replace(JsonEncodedText.Encode(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).ToString(), "/home/dev", StringComparison.Ordinal);
        });

        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, $"{name}.{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl"), clean);
    }
}
