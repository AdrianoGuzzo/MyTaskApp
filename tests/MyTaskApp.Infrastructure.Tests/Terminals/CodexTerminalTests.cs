using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Infrastructure.Agents.Codex;
using MyTaskApp.Infrastructure.Agents;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Terminals.Linux;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

public class CodexTerminalTests
{
    [Fact]
    public async Task InstalledCodexDeliversSessionStartThroughItsConfiguredHook()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MYTASKAPP_TEST_CODEX") == "1", "Defina MYTASKAPP_TEST_CODEX=1 para verificar o Codex real.");
        if (!OperatingSystem.IsWindows())
            return;
        var ct = TestContext.Current.CancellationToken;
        var delivered = new TaskCompletionSource<RecordAgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new AgentEventListener((command, _) =>
        {
            delivered.TrySetResult(command);
            return Task.CompletedTask;
        }, 0, TimeProvider.System, NullLogger<AgentEventListener>.Instance);
        listener.Start();
        var provider = new CodexCliProvider(ExecutableLocator.ForCurrentSystem(), new ProcessRunner());
        var detection = await provider.DetectAsync(ct);
        Assert.SkipUnless(detection.IsInstalled, "O Codex não está instalado.");
        var environment = new Dictionary<string, string>
        {
            [AgentMonitoringEnvironment.HookToken] = "test-token",
            [AgentMonitoringEnvironment.AgentSessionId] = Guid.NewGuid().ToString(),
            [AgentMonitoringEnvironment.TaskId] = Guid.NewGuid().ToString(),
        };
        var folder = Directory.CreateTempSubdirectory("mytaskapp-codex-hooks-");
        environment["CODEX_HOME"] = folder.FullName;
        try
        {
            var launch = provider.CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), folder.FullName,
                ["exec", "--skip-git-repo-check", "--dangerously-bypass-hook-trust", "-c", "features.hooks=true",
                 "-c", "model_provider=\"mytaskapp-test\"",
                 "-c", "model_providers.mytaskapp-test={name=\"Teste\",base_url=\"http://localhost:1\",wire_api=\"responses\",request_max_retries=0,stream_max_retries=0}",
                 "-c", "model=\"test-model\""],
                "Return OK.", RunDirectly: true,
                Monitoring: new AgentMonitoring(listener.Address!, environment)), detection);
            var variables = environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
            variables["CODEX_THREAD_ID"] = null;
            variables["CODEX_TURN_ID"] = null;
            // O backend fictício impede chamadas ao modelo; o timeout encerra o processo.
            var result = await new ProcessRunner().RunAsync(new ProcessRequest(launch.Executable, launch.Arguments,
                TimeSpan.FromSeconds(5), folder.FullName, variables), ct);
            delivered.Task.IsCompleted.Should().BeTrue(result.StandardOutput + result.StandardError);
            (await delivered.Task).Event.Type.Should().Be(AgentEventType.SessionStarted);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder.FullName, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstalledCodexReportsItsVersionThroughTheHostedPseudoTerminalWithoutCallingAModel()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MYTASKAPP_TEST_CODEX") == "1",
            "Defina MYTASKAPP_TEST_CODEX=1 para verificar o Codex real, sem chamar o modelo.");
        var cancellationToken = TestContext.Current.CancellationToken;
        IPseudoTerminalFactory? factory = OperatingSystem.IsWindows()
            ? new ConPtyTerminalFactory(NullLogger<ConPtyTerminal>.Instance)
            : OperatingSystem.IsLinux() ? new UnixPtyTerminalFactory(NullLogger<UnixPtyTerminal>.Instance) : null;
        Assert.SkipWhen(factory is not { Support.IsSupported: true }, "Este sistema não suporta pseudoterminais.");
        var provider = new CodexCliProvider(ExecutableLocator.ForCurrentSystem(), new ProcessRunner());
        var detection = await provider.DetectAsync(cancellationToken);
        Assert.SkipUnless(detection.IsInstalled, "O Codex não está instalado.");
        var folder = Directory.CreateTempSubdirectory("mytaskapp-codex-pty-");
        try
        {
            var launch = provider.CreateLaunch(new AgentCliStartContext(Guid.NewGuid(), folder.FullName, ["--version"],
                Terminal: new AgentCliTerminal(Guid.NewGuid().ToString(), false)), detection);
            await using var harness = new PtyHarness(factory!.Start(new PseudoTerminalStart(launch.Executable, launch.Arguments,
                launch.WorkingDirectory, launch.Environment, new TerminalSize(100, 30), launch.RemovedVariables)));
            (await harness.WaitForAsync("codex-cli")).Should().MatchRegex(@"\d+\.\d+\.\d+");
            (await harness.Terminal.Exited.WaitAsync(PtyHarness.Patience, cancellationToken)).Should().Be(0);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
