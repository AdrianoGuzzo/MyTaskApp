using MyTaskApp.Desktop.Development;

namespace MyTaskApp.Desktop.Tests.Development;

/// <summary>As instruções de instalação do Git, por sistema (ADR-027).</summary>
public class GitInstallGuideTests
{
    [Fact]
    public void Windows_RecommendsWinget_AndTheOfficialPage()
    {
        var guide = GitInstallGuide.ForWindows();

        guide.SystemName.Should().Be("Windows");
        guide.Commands.Should().ContainSingle().Which.Command
            .Should().Be("winget install --id Git.Git -e --source winget");
        guide.OfficialSite.Should().Be(new Uri("https://git-scm.com/downloads/win"));
        guide.Verify.Command.Should().Be("git --version");
    }

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"", "sudo apt install git", "Ubuntu 24.04 LTS")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"", "sudo apt install git", "Linux")]
    [InlineData("ID=debian\nNAME=\"Debian GNU/Linux\"", "sudo apt install git", "Debian GNU/Linux")]
    [InlineData("ID=fedora\nPRETTY_NAME=\"Fedora Linux 42\"", "sudo dnf install git", "Fedora Linux 42")]
    [InlineData("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"", "sudo dnf install git", "Linux")]
    [InlineData("ID=arch", "sudo pacman -S git", "Linux")]
    [InlineData("ID=manjaro\nID_LIKE=arch", "sudo pacman -S git", "Linux")]
    public void Linux_PicksThePackageManagerOfTheDistribution(string osRelease, string command, string name)
    {
        var guide = GitInstallGuide.ForLinux(osRelease);

        guide.Commands.Should().ContainSingle().Which.Command.Should().Be(command);
        guide.SystemName.Should().Be(name);
        guide.OfficialSite.Should().Be(new Uri("https://git-scm.com/downloads/linux"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ID=gentoo")]
    public void Linux_Unknown_ShowsTheThreeCommonOnes(string? osRelease)
    {
        var guide = GitInstallGuide.ForLinux(osRelease);

        guide.Commands.Select(command => command.Command).Should().Equal(
            "sudo apt install git",
            "sudo dnf install git",
            "sudo pacman -S git");
    }

    [Fact]
    public void TheCurrentSystem_AlwaysHasSomethingToShow() =>
        GitInstallGuide.ForCurrentSystem().Commands.Should().NotBeEmpty();
}
