using MyTaskApp.Desktop.Development;

namespace MyTaskApp.Desktop.Tests.Development;

/// <summary>Como abrir um terminal já dentro do worktree, em cada sistema (ADR-027).</summary>
public class TerminalCommandPlannerTests
{
    private const string Path = @"C:\Projects\eco core-feature-x";

    [Fact]
    public void Windows_TriesWindowsTerminal_ThenPowerShellStartingInTheFolder()
    {
        var candidates = TerminalCommandPlanner.Candidates(TerminalCommandPlanner.Platform.Windows, Path);

        candidates[0].FileName.Should().Be("wt.exe");
        candidates[0].Arguments.Should().Equal("-d", Path);
        candidates[1].FileName.Should().Be("powershell.exe");
        candidates[1].WorkingDirectory.Should().Be(Path);
    }

    /// <summary>Nenhum candidato passa por "cmd /c": o caminho vai sempre como argumento.</summary>
    [Theory]
    [InlineData(TerminalCommandPlanner.Platform.Windows)]
    [InlineData(TerminalCommandPlanner.Platform.Linux)]
    [InlineData(TerminalCommandPlanner.Platform.MacOs)]
    public void NoCandidate_GoesThroughAShellCommandLine(TerminalCommandPlanner.Platform platform)
    {
        foreach (var candidate in TerminalCommandPlanner.Candidates(platform, Path))
        {
            new[] { "cmd", "cmd.exe", "sh", "bash" }.Should().NotContain(candidate.FileName);
            candidate.Arguments.Should().NotContain("/c").And.NotContain("-c");
            (candidate.WorkingDirectory == Path || candidate.Arguments.Any(argument => argument.Contains(Path, StringComparison.Ordinal)))
                .Should().BeTrue();
        }
    }

    [Fact]
    public void Linux_TriesTheCommonDesktops()
    {
        TerminalCommandPlanner.Candidates(TerminalCommandPlanner.Platform.Linux, "/home/u/eco")
            .Select(candidate => candidate.FileName)
            .Should().Equal("gnome-terminal", "konsole", "xfce4-terminal", "x-terminal-emulator", "xterm");
    }

    [Fact]
    public void MacOs_OpensTerminalApp() =>
        TerminalCommandPlanner.Candidates(TerminalCommandPlanner.Platform.MacOs, "/Users/u/eco")
            .Should().ContainSingle().Which.Arguments.Should().Equal("-a", "Terminal", "/Users/u/eco");
}
