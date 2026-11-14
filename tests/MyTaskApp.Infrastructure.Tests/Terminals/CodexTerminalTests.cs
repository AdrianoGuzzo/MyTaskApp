using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Infrastructure.Agents.Codex;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Terminals.Linux;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

public class CodexTerminalTests
{
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
