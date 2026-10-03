using MyTaskApp.Infrastructure.GitHub;

namespace MyTaskApp.Infrastructure.Tests.GitHub;

/// <summary>
/// Onde o GitHub CLI é procurado (ADR-047). Como no Git: instalar com o app
/// aberto e o "Verificar novamente" encontrar, sem reiniciar.
/// </summary>
public class GhLocatorTests
{
    [Fact]
    public void Windows_RereadsThePath_FromTheRegistry()
    {
        var locator = new GhLocator(
            (name, target) => (name, target) is ("PATH", EnvironmentVariableTarget.User) ? @"C:\Tools" : null,
            path => path == @"C:\Tools\gh.exe",
            isWindows: true);

        locator.Locate().Should().Be(@"C:\Tools\gh.exe");
    }

    /// <summary>O winget instala o MSI, que vai para "Program Files\GitHub CLI".</summary>
    [Fact]
    public void Windows_FindsTheDefaultInstallFolder_EvenWithAStalePath()
    {
        var locator = new GhLocator(
            (name, _) => name switch
            {
                "ProgramFiles" => @"C:\Program Files",
                "PATH" => @"C:\Windows",
                _ => null,
            },
            path => path == @"C:\Program Files\GitHub CLI\gh.exe",
            isWindows: true);

        locator.Locate().Should().Be(@"C:\Program Files\GitHub CLI\gh.exe");
    }

    [Fact]
    public void Unix_FindsHomebrew()
    {
        // O Path.Combine do Windows junta com "\"; a regra é do macOS e do Linux.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Caminhos do Unix.");

        var locator = new GhLocator((_, _) => null, path => path == "/opt/homebrew/bin/gh", isWindows: false);

        locator.Locate().Should().Be("/opt/homebrew/bin/gh");
    }

    [Fact]
    public void Missing_IsNull()
    {
        var locator = new GhLocator((_, _) => null, _ => false, isWindows: true);

        locator.Locate().Should().BeNull();
    }
}
