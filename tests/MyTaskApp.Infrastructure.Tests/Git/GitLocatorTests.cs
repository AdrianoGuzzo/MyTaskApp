using MyTaskApp.Infrastructure.Git;

namespace MyTaskApp.Infrastructure.Tests.Git;

/// <summary>
/// Onde o Git é procurado (ADR-027). O caso que importa: instalar o Git com o
/// app aberto e o "Verificar novamente" encontrar, sem reiniciar.
/// </summary>
public class GitLocatorTests
{
    [Fact]
    public void Windows_RereadsMachineAndUserPath_BeforeTheProcessOne()
    {
        var environment = new Dictionary<(string, EnvironmentVariableTarget), string>
        {
            [("PATH", EnvironmentVariableTarget.Machine)] = @"C:\Windows;C:\Program Files\Git\cmd",
            [("PATH", EnvironmentVariableTarget.User)] = @"C:\Users\u\bin",
            [("PATH", EnvironmentVariableTarget.Process)] = @"C:\Windows",
        };

        var locator = new GitLocator(
            (name, target) => environment.GetValueOrDefault((name, target)),
            _ => false,
            isWindows: true);

        locator.Candidates().Should().StartWith(
        [
            @"C:\Windows\git.exe",
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Users\u\bin\git.exe",
        ]);
    }

    /// <summary>O PATH do processo nasceu antes da instalação; a pasta padrão do instalador salva.</summary>
    [Fact]
    public void Windows_FindsTheDefaultInstallFolder_EvenWithAStalePath()
    {
        var locator = new GitLocator(
            (name, _) => name switch
            {
                "ProgramFiles" => @"C:\Program Files",
                "PATH" => @"C:\Windows",
                _ => null,
            },
            path => path == @"C:\Program Files\Git\cmd\git.exe",
            isWindows: true);

        locator.Locate().Should().Be(@"C:\Program Files\Git\cmd\git.exe");
    }

    [Fact]
    public void Windows_FindsAPerUserInstall()
    {
        var locator = new GitLocator(
            (name, _) => name == "LOCALAPPDATA" ? @"C:\Users\u\AppData\Local" : null,
            path => path == @"C:\Users\u\AppData\Local\Programs\Git\cmd\git.exe",
            isWindows: true);

        locator.Locate().Should().Be(@"C:\Users\u\AppData\Local\Programs\Git\cmd\git.exe");
    }

    [Fact]
    public void Linux_UsesTheProcessPath_ThenTheUsualFolders()
    {
        // "/usr/bin" não é caminho completo no Windows; a regra é do Linux.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Caminhos do Linux.");

        var locator = new GitLocator(
            (name, target) => name == "PATH" && target == EnvironmentVariableTarget.Process ? "/home/u/bin:/usr/bin" : null,
            _ => false,
            isWindows: false);

        locator.Candidates().Should().Equal(
            "/home/u/bin/git",
            "/usr/bin/git",
            "/usr/local/bin/git",
            "/opt/homebrew/bin/git");
    }

    [Fact]
    public void NothingFound_IsNull()
    {
        var locator = new GitLocator((_, _) => null, _ => false, isWindows: true);

        locator.Locate().Should().BeNull();
    }

    [Fact]
    public void RelativeEntriesInPath_AreIgnored()
    {
        var locator = new GitLocator(
            (name, target) => name == "PATH" && target == EnvironmentVariableTarget.Process ? @".;bin;C:\Tools" : null,
            _ => false,
            isWindows: true);

        locator.Candidates().Should().StartWith([@"C:\Tools\git.exe"]);
    }
}
