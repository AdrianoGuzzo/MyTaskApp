using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Terminals.Linux;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// O terminal do Linux (fase 3, E9): o primeiro instalado que vive enquanto o
/// programa vive, com a pasta, o ambiente e o programa como argumentos
/// separados. Aqui só o plano; abrir a janela pede um Linux com tela.
/// </summary>
public sealed class LinuxTerminalLauncherTests
{
    private const string Claude = "/home/ana/.local/bin/claude";

    private const string Worktree = "/home/ana/src/app-feature x";

    private static LinuxTerminalLauncher Launcher(params string[] installed) =>
        new(
            name => installed.Contains(name) ? $"/usr/bin/{name}" : null,
            path => path == Claude,
            path => path == Worktree,
            NullLogger<LinuxTerminalLauncher>.Instance);

    [Fact]
    public void TheFirstInstalledTerminal_KeepsTheWindowTiedToTheProgram()
    {
        var plan = Launcher("xterm", "konsole").Plan(new TerminalLaunchOptions(Claude, ["--resume", "abc"], Worktree));

        plan.Error.Should().BeNull();
        plan.FileName.Should().Be("/usr/bin/konsole");
        plan.Arguments.Should().Equal(
            "--nofork", "-e",
            LinuxTerminalLauncher.Shell, "-c", LinuxTerminalLauncher.Script, "mytaskapp", Worktree,
            Claude, "--resume", "abc");
    }

    [Fact]
    public void TheEnvironment_GoesThroughEnv_AsSeparateArguments()
    {
        var environment = new Dictionary<string, string> { ["MYTASKAPP_TASK_ID"] = "42", ["MYTASKAPP_TITLE"] = "a\"; rm -rf ~" };

        var plan = Launcher("gnome-terminal").Plan(new TerminalLaunchOptions(Claude, [], Worktree, environment));

        plan.FileName.Should().Be("/usr/bin/gnome-terminal");
        plan.Arguments.Should().Equal(
            "--wait", "--",
            LinuxTerminalLauncher.Shell, "-c", LinuxTerminalLauncher.Script, "mytaskapp", Worktree,
            LinuxTerminalLauncher.Env, "MYTASKAPP_TASK_ID=42", "MYTASKAPP_TITLE=a\"; rm -rf ~",
            Claude);
    }

    [Fact]
    public void WithoutAKnownTerminal_SaysWhichOnesToInstall()
    {
        Launcher().Plan(new TerminalLaunchOptions(Claude, [], Worktree)).Error.Should().Be(LinuxTerminalLauncher.NoTerminal);
    }

    [Theory]
    [InlineData("claude", Worktree)]
    [InlineData("/usr/bin/missing", Worktree)]
    [InlineData(Claude, "relative/folder")]
    [InlineData(Claude, "/missing")]
    public void AProgramOrFolderThatIsNotThere_IsRefusedBeforeAnyTerminal(string executable, string folder)
    {
        Launcher("xterm").Plan(new TerminalLaunchOptions(executable, [], folder)).FileName.Should().BeNull();
    }

    /// <summary>A linha crua é do cmd do Windows (ADR-051).</summary>
    [Fact]
    public void ARawLine_IsRefused()
    {
        Launcher("xterm").Plan(new TerminalLaunchOptions(Claude, [], Worktree, RawArguments: "--resume abc")).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task AFailedPlan_ComesBackAsAFailedLaunch_WithoutThrowing()
    {
        var result = await Launcher().LaunchAsync(new TerminalLaunchOptions(Claude, [], Worktree), TestContext.Current.CancellationToken);

        result.Started.Should().BeFalse();
        result.Error.Should().Be(LinuxTerminalLauncher.NoTerminal);
    }
}
