using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O Claude Code numa Consulta Inteligente (ADR-081), sem Claude nenhum: o que
/// o terminal executa, o que vai para os arquivos e o que fica só no ambiente.
/// </summary>
public sealed class ClaudeCodeConsultationLaunchTests : IDisposable
{
    private const string Native = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Npm = @"C:\Users\dev\AppData\Roaming\npm\claude.cmd";
    private const string Api = @"C:\Projetos\loja api";
    private const string Web = @"C:\Projetos\loja-web";

    private static readonly Uri Mcp = new("http://127.0.0.1:5197/mcp");
    private static readonly Uri Events = new("http://127.0.0.1:47831/api/claude/events");

    private readonly string _state = Path.Combine(Path.GetTempPath(), "mytaskapp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private ClaudeCodeCliProvider Provider() =>
        new(
            new ExecutableLocator((_, _) => null, _ => false, isWindows: true),
            new NoProcessRunner(),
            new ClaudeCodeHooks(_state, Path.Combine(_state, "home"), [], NullLogger<ClaudeCodeHooks>.Instance),
            NullLogger<ClaudeCodeCliProvider>.Instance);

    private static CliDetectionResult Installed(string executable = Native) =>
        new() { IsInstalled = true, ExecutablePath = executable };

    private static ConsultationLaunchRequest Request(
        bool withTools = false,
        string? question = "Por que o pedido 1234 está pendente?",
        bool resume = false,
        AgentMonitoring? monitoring = null) =>
        new(
            Guid.Parse("0199f0a1-0000-7000-8000-000000000001"),
            "6f1c2a8e-1b2c-4d5e-8f90-123456789abc",
            resume,
            "Consulta · Pedido \"pendente\" 100%",
            [Api, Web],
            "# Consulta Inteligente\n\nSó leia.",
            question,
            withTools ? new ConsultationToolsAccess(Mcp, ConsultationToolNames.TokenVariable, ConsultationToolNames.All) : null,
            ["--model", "opus"],
            withTools ? new Dictionary<string, string> { [ConsultationToolNames.TokenVariable] = new string('A', 64) } : new Dictionary<string, string>(),
            monitoring);

    private static string? After(IReadOnlyList<string> arguments, string option) =>
        arguments.ToList().IndexOf(option) is var index and >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;

    [Fact]
    public void OnlyReading_NoCommands_NoUserMcpServers_AndNeverSkippingPermissions()
    {
        var launch = Provider().CreateConsultationLaunch(Request(), Installed());

        launch.Executable.Should().Be(Native);
        launch.WorkingDirectory.Should().Be(Api);
        launch.Arguments.Should().Contain(["--restricted", "--strict-mcp-config"]);
        After(launch.Arguments, "--tools").Should().Be("Read,Grep,Glob");
        launch.Arguments.Should().NotContain(argument => argument.Contains("dangerously", StringComparison.Ordinal));
        launch.Arguments.Should().NotContain(["--mcp-config", "--allowedTools", "--permission-mode"]);
    }

    [Fact]
    public void TheConversation_StartsWithItsId_AndTheQuestionLast()
    {
        var launch = Provider().CreateConsultationLaunch(Request(), Installed());

        After(launch.Arguments, "--session-id").Should().Be("6f1c2a8e-1b2c-4d5e-8f90-123456789abc");
        launch.Arguments[^1].Should().Be("Por que o pedido 1234 está pendente?");
        launch.Arguments[^3].Should().Be("--append-system-prompt-file");
        launch.Arguments.Should().ContainInConsecutiveOrder("--model", "opus");
    }

    [Fact]
    public void Resuming_UsesTheSameConversation_WithoutAQuestion()
    {
        var launch = Provider().CreateConsultationLaunch(Request(question: null, resume: true), Installed());

        After(launch.Arguments, "--resume").Should().Be("6f1c2a8e-1b2c-4d5e-8f90-123456789abc");
        launch.Arguments.Should().NotContain("--session-id");
        launch.Arguments[^2].Should().Be("--append-system-prompt-file");
    }

    [Fact]
    public void TheOtherFolders_AreAdditional()
    {
        var launch = Provider().CreateConsultationLaunch(Request(), Installed());

        After(launch.Arguments, "--add-dir").Should().Be(Web);
        launch.Arguments.Count(argument => argument == "--add-dir").Should().Be(1);
        launch.Arguments.Should().NotContain(Api);
    }

    [Fact]
    public void TheInstructions_GoInAFile()
    {
        var launch = Provider().CreateConsultationLaunch(Request(), Installed());

        var file = After(launch.Arguments, "--append-system-prompt-file")!;
        file.Should().StartWith(_state);
        File.ReadAllText(file).Should().Be("# Consulta Inteligente\n\nSó leia.");
    }

    [Fact]
    public void TheName_DropsWhatTheShellWouldRead()
    {
        var launch = Provider().CreateConsultationLaunch(Request(), Installed());

        After(launch.Arguments, "--name").Should().Be("Consulta · Pedido pendente 100");
    }

    [Fact]
    public void WithTheDatabase_OnlyTheAppServer_AndOnlyTheConsultationTools_ArePreapproved()
    {
        var launch = Provider().CreateConsultationLaunch(Request(withTools: true), Installed());

        After(launch.Arguments, "--allowedTools").Should().Be(
            "mcp__mytaskapp__consultation_database_describe,mcp__mytaskapp__consultation_table_list,"
            + "mcp__mytaskapp__consultation_table_describe,mcp__mytaskapp__consultation_query");

        using var config = JsonDocument.Parse(File.ReadAllText(After(launch.Arguments, "--mcp-config")!));
        var servers = config.RootElement.GetProperty("mcpServers");
        servers.EnumerateObject().Select(server => server.Name).Should().Equal("mytaskapp");
        servers.GetProperty("mytaskapp").GetProperty("url").GetString().Should().Be(Mcp.ToString());
        servers.GetProperty("mytaskapp").GetProperty("headers").GetProperty("Authorization").GetString()
            .Should().Be("Bearer ${MYTASKAPP_CONSULTATION_TOKEN}");
    }

    [Fact]
    public void TheSecret_IsOnlyInTheEnvironment()
    {
        var secret = new string('A', 64);

        var launch = Provider().CreateConsultationLaunch(Request(withTools: true), Installed());

        launch.Environment![ConsultationToolNames.TokenVariable].Should().Be(secret);
        launch.Arguments.Should().NotContain(argument => argument.Contains(secret, StringComparison.Ordinal));

        foreach (var file in Directory.EnumerateFiles(_state, "*", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).Should().NotContain(secret);
        }
    }

    [Fact]
    public void WithMonitoring_TheHooksComeFirst_AndTheirEnvironmentJoins()
    {
        var monitoring = new AgentMonitoring(Events, new Dictionary<string, string> { [AgentMonitoringEnvironment.HookToken] = "gancho" });

        var launch = Provider().CreateConsultationLaunch(Request(withTools: true, monitoring: monitoring), Installed());

        launch.Arguments[0].Should().Be("--settings");
        launch.Environment!.Should().ContainKey(AgentMonitoringEnvironment.HookToken)
            .And.ContainKey(ConsultationToolNames.TokenVariable);
    }

    [Fact]
    public void ThroughClaudeCmd_AQuestionWithQuotes_MovesToTheInstructions()
    {
        var request = Request(question: "O que significa \"pendente\" em 100% dos casos?");

        var launch = Provider().CreateConsultationLaunch(request, Installed(Npm));

        launch.Arguments[^1].Should().NotContain("\"").And.NotContain("%");
        File.ReadAllText(After(launch.Arguments, "--append-system-prompt-file")!)
            .Should().Contain("## Pergunta inicial").And.Contain("O que significa \"pendente\" em 100% dos casos?");
    }

    [Fact]
    public void ThroughClaudeCmd_AMultilineQuestion_BecomesOneLine()
    {
        var launch = Provider().CreateConsultationLaunch(Request(question: "Por que\npendente?"), Installed(Npm));

        launch.Arguments[^1].Should().Be("Por que pendente?");
    }

    /// <summary>Ninguém pergunta a versão aqui: abrir a consulta não roda processo nenhum.</summary>
    private sealed class NoProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
    }
}
