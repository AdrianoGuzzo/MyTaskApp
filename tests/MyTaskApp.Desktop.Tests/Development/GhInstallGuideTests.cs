using MyTaskApp.Desktop.Development;

namespace MyTaskApp.Desktop.Tests.Development;

/// <summary>As instruções de instalação do GitHub CLI, por sistema (ADR-047).</summary>
public class GhInstallGuideTests
{
    [Fact]
    public void Windows_RecommendsWinget()
    {
        var guide = GhInstallGuide.ForWindows();

        guide.SystemName.Should().Be("Windows");
        guide.Commands.Should().ContainSingle().Which.Command
            .Should().Be("winget install --id GitHub.cli -e --source winget");
        guide.OfficialSite.Should().Be(new Uri("https://cli.github.com/"));
    }

    [Fact]
    public void MacOs_UsesHomebrew() =>
        GhInstallGuide.ForMacOs().Commands.Should().ContainSingle().Which.Command.Should().Be("brew install gh");

    /// <summary>Instalar não basta: o app usa o login do gh, e é ele que se confere.</summary>
    [Fact]
    public void EverySystem_EndsWithLoginAndCheck()
    {
        foreach (var guide in new[] { GhInstallGuide.ForWindows(), GhInstallGuide.ForMacOs(), GhInstallGuide.ForLinux(null) })
        {
            guide.Login.Command.Should().Be("gh auth login");
            guide.Verify.Command.Should().Be("gh auth status");
        }
    }

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"", "sudo apt install gh", "Ubuntu 24.04 LTS")]
    [InlineData("ID=fedora\nPRETTY_NAME=\"Fedora Linux 42\"", "sudo dnf install gh", "Fedora Linux 42")]
    [InlineData("ID=manjaro\nID_LIKE=arch", "sudo pacman -S github-cli", "Linux")]
    public void Linux_PicksThePackageManagerOfTheDistribution(string osRelease, string command, string name)
    {
        var guide = GhInstallGuide.ForLinux(osRelease);

        guide.Commands.Should().ContainSingle().Which.Command.Should().Be(command);
        guide.SystemName.Should().Be(name);
    }

    [Fact]
    public void Linux_Unknown_ShowsTheThreeCommonOnes() =>
        GhInstallGuide.ForLinux("ID=gentoo").Commands.Select(command => command.Command).Should().Equal(
            "sudo apt install gh",
            "sudo dnf install gh",
            "sudo pacman -S github-cli");
}
