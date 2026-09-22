using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// Como o processo foi lançado (ADR-023). São duas decisões saindo de um
/// argumento: se a janela aparece, e se um segundo lançamento revela a do
/// primeiro.
/// </summary>
public class LaunchOptionsTests
{
    [Fact]
    public void WithoutArguments_TheLaunchCameFromAPerson()
    {
        var launch = LaunchOptions.Parse([]);

        launch.StartedByWindows.Should().BeFalse();
        launch.ShouldSignalExistingInstance.Should().BeTrue();
    }

    [Fact]
    public void TheStartupFlagMarksTheLaunchAsComingFromTheLogin()
    {
        var launch = LaunchOptions.Parse([LaunchOptions.StartupFlag]);

        launch.StartedByWindows.Should().BeTrue();
    }

    [Fact]
    public void TheFlagIsFoundAmongTheArgumentsAvaloniaAlsoGets()
    {
        // O Main repassa args inteiro para o StartWithClassicDesktopLifetime:
        // o nosso argumento não é nem o primeiro nem o único.
        var launch = LaunchOptions.Parse(["--enable-gpu", LaunchOptions.StartupFlag, "--whatever"]);

        launch.StartedByWindows.Should().BeTrue();
    }

    [Fact]
    public void TheFlagIsRecognisedWhateverTheCase()
    {
        // O valor gravado em Run pode ter sido editado à mão por alguém.
        var launch = LaunchOptions.Parse(["--STARTUP"]);

        launch.StartedByWindows.Should().BeTrue();
    }

    [Fact]
    public void SomethingElseEntirely_IsNotTheStartupFlag()
    {
        LaunchOptions.Parse(["--start"]).StartedByWindows.Should().BeFalse();
        LaunchOptions.Parse(["startup"]).StartedByWindows.Should().BeFalse();
    }

    [Fact]
    public void ALaunchFromTheLogin_NeverRevealsTheWindowOfTheAppAlreadyRunning()
    {
        // O clique no atalho traz o painel de volta (ADR-019). O login não é um
        // clique: ninguém pediu nada, e a janela pulando na frente do usuário no
        // boot é exatamente o que "recolhido na bandeja" promete não fazer.
        var launch = LaunchOptions.Parse([LaunchOptions.StartupFlag]);

        launch.ShouldSignalExistingInstance.Should().BeFalse();
    }

    [Fact]
    public void TheManualLaunchIsTheDefault()
    {
        LaunchOptions.Manual.StartedByWindows.Should().BeFalse();
        LaunchOptions.Manual.ShouldSignalExistingInstance.Should().BeTrue();
    }
}
