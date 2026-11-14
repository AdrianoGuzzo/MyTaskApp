using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Agents;
using MyTaskApp.Infrastructure.Agents.Codex;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

public class CodexCliProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Runner _runner = new();
    private CodexCliProvider Provider(bool installed = true) => new(new ExecutableLocator(
        (key, _) => key is "PATH" ? @"C:\tools" : null,
        path => installed && (path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase)), true), _runner);
    private static CliDetectionResult Detection => new() { IsInstalled = true, ExecutablePath = @"C:\tools\codex.exe" };

    [Fact]
    public async Task DetectionReadsVersionWithoutStartingAnInteractiveSession()
    {
        var result = await Provider().DetectAsync(Ct);
        result.IsInstalled.Should().BeTrue();
        result.Version.Should().Be("0.169.0");
        _runner.Requests.Single().Arguments.Should().Equal("--version");
    }

    [Fact]
    public async Task MissingInstallationDoesNotStartAProcess()
    {
        (await Provider(false).DetectAsync(Ct)).IsInstalled.Should().BeFalse();
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WindowsNpmInstallationUsesItsBundledNativeExecutable()
    {
        var npm = Path.Combine(@"C:\Users\dev", "npm");
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var target = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var native = Path.Combine(npm, "node_modules", "@openai", "codex", "node_modules", "@openai",
            $"codex-win32-{architecture}", "vendor", target, "bin", "codex.exe");
        var provider = new CodexCliProvider(new ExecutableLocator((key, _) => key == "PATH" ? npm : null,
            path => path == Path.Combine(npm, "codex.cmd") || path == native, true), _runner);
        (await provider.DetectAsync(Ct)).ExecutablePath.Should().Be(native);
        _runner.Requests.Single().FileName.Should().Be(native);
    }

    [Fact]
    public void InteractiveDefaultBypassesApprovalsAndSandbox()
    {
        var provider = Provider();
        provider.DefaultArguments.Should().Be("--dangerously-bypass-approvals-and-sandbox");
        provider.BypassesPermissions([provider.DefaultArguments], @"C:\work").Should().BeTrue();
        provider.BypassesPermissions([], @"C:\work").Should().BeFalse();
    }

    [Fact]
    public void NativeLaunchPreservesPromptAsOneArgumentAndSupportsHostedTerminal()
    {
        const string prompt = "Confira \"um arquivo\"\ncom % e &";
        var launch = Provider().CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), @"C:\work", [], prompt,
            RunDirectly: true, Terminal: new AgentCliTerminal(Guid.NewGuid().ToString(), false)), Detection);
        launch.Arguments.Should().Equal(prompt);
        launch.WorkingDirectory.Should().Be(@"C:\work");
    }

    [Fact]
    public void ResumeTargetsTheExactConversation()
    {
        var id = Guid.NewGuid().ToString();
        var launch = Provider().CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), @"C:\work", [],
            Terminal: new AgentCliTerminal(id, true)), Detection);
        launch.Arguments.Should().Equal("resume", id);
    }

    [Fact]
    public void PlanRequestRemovesBypassAndEnforcesReadOnlySandbox()
    {
        var provider = Provider();
        var launch = provider.CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), @"C:\work",
            [provider.DefaultArguments], "Planeje"), Detection);
        launch.Arguments.Should().NotContain(provider.DefaultArguments);
        launch.Arguments.Should().ContainInOrder("--sandbox", "read-only");
    }

    [Fact]
    public void MonitoringUsesEnvironmentReferencesRatherThanEmbeddingSecrets()
    {
        var environment = new Dictionary<string, string> { [AgentMonitoringEnvironment.HookToken] = "secret-value" };
        var launch = Provider().CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), @"C:\work", [],
            Monitoring: new AgentMonitoring(new Uri("http://localhost:47831/api/claude/events"), environment)), Detection);
        string.Join(' ', launch.Arguments).Should().Contain("hooks.PermissionRequest=").And.NotContain("secret-value");
        launch.Environment.Should().BeSameAs(environment);
    }

    [Fact]
    public void ConsultationKeepsDatabaseTokenInEnvironmentAndForcesReadOnly()
    {
        var provider = Provider();
        var environment = new Dictionary<string, string> { [ConsultationToolNames.TokenVariable] = "secret-value" };
        var launch = provider.CreateConsultationLaunch(new ConsultationLaunchRequest(Guid.NewGuid(), Guid.NewGuid().ToString(),
            false, "Consulta", [@"C:\work"], "Somente leitura", "Consulte",
            new ConsultationToolsAccess(new Uri("http://localhost:47830/mcp"), ConsultationToolNames.TokenVariable, ConsultationToolNames.All),
            [provider.DefaultArguments], environment, null), Detection);
        launch.Arguments.Should().NotContain(provider.DefaultArguments).And.ContainInOrder("--sandbox", "read-only");
        string.Join(' ', launch.Arguments).Should().Contain("bearer_token_env_var").And.NotContain("secret-value");
        launch.Environment.Should().BeSameAs(environment);
    }

    [Fact]
    public void ManagedConversationDoesNotSilentlyDropTheBudgetGuarantee()
    {
        Provider().HeadlessUnavailableReason(Detection).Should().Contain("teto de custo");
    }

    [Fact]
    public void StructuredHooksIdentifyTheActualCodexConversation()
    {
        using var payload = JsonDocument.Parse("""{"hook_event_name":"SessionStart","session_id":"codex-thread","cwd":"C:/work"}""");
        var received = CodexHookEvents.Translate(payload.RootElement, Guid.NewGuid(), null, DateTimeOffset.UnixEpoch);
        received!.ExternalSessionId.Should().Be("codex-thread");
        received.Type.Should().Be(AgentEventType.SessionStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsHookCommandDeliversAnAuthenticatedEvent(bool escapeArgument)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var delivered = new TaskCompletionSource<RecordAgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new AgentEventListener((command, _) =>
        {
            delivered.TrySetResult(command);
            return Task.CompletedTask;
        }, 0, TimeProvider.System, NullLogger<AgentEventListener>.Instance);
        listener.Start();
        var provider = new CodexCliProvider(ExecutableLocator.ForCurrentSystem(), _runner);
        var sessionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var environment = new Dictionary<string, string>
        {
            [AgentMonitoringEnvironment.HookToken] = "test-token",
            [AgentMonitoringEnvironment.AgentSessionId] = sessionId.ToString(),
            [AgentMonitoringEnvironment.TaskId] = taskId.ToString(),
        };
        var launch = provider.CreateLaunch(new AgentCliStartContext(taskId, Path.GetTempPath(), [],
            Monitoring: new AgentMonitoring(listener.Address!, environment)), Detection);
        var setting = launch.Arguments.Single(argument => argument.StartsWith("hooks.SessionStart=", StringComparison.Ordinal));
        var serializedCommand = setting[(setting.IndexOf("command=", StringComparison.Ordinal) + 8)..setting.LastIndexOf(",timeout=", StringComparison.Ordinal)];
        var commandLine = JsonSerializer.Deserialize<string>(serializedCommand)!;
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (escapeArgument)
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(commandLine);
        }
        else
        {
            start.Arguments = "/c " + commandLine;
        }
        foreach (var pair in environment)
            start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("""{"hook_event_name":"SessionStart","session_id":"codex-thread","cwd":"C:/revisão"}""");
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        process.ExitCode.Should().Be(0, error);
        output.Should().Be("{}");
        var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        received.Token.Should().Be("test-token");
        received.Event.AgentSessionId.Should().Be(sessionId);
        received.Event.TaskId.Should().Be(taskId);
        received.Event.WorkingDirectory.Should().Be("C:/revisão");
    }

    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, "codex-cli 0.169.0", "", false));
        }
    }
}
