using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// "Iniciar com o Windows" visto da moldura do painel (ADR-023). A verdade mora
/// na chave <c>Run</c>, e tudo aqui existe para o visto do menu não inventar uma
/// segunda versão dela.
/// </summary>
public class WidgetStartupTests
{
    [Fact]
    public void BeforeTheRegistryIsWiredIn_TheOptionSimplyIsNotThere()
    {
        // É o estado do designer e dos testes que sobem a janela sem contêiner:
        // a moldura funciona igual, só não sabe iniciar com o Windows.
        var chrome = new WidgetChromeViewModel();

        chrome.CanStartWithWindows.Should().BeFalse();
        chrome.StartsWithWindows.Should().BeFalse();
    }

    [Fact]
    public void WiringItInShowsWhatTheRegistryAlreadySays()
    {
        var startup = new FakeStartupRegistration { IsEnabled = true };
        var chrome = new WidgetChromeViewModel();

        chrome.UseStartup(startup);

        chrome.CanStartWithWindows.Should().BeTrue();
        chrome.StartsWithWindows.Should().BeTrue();
    }

    [Fact]
    public void TurningItOnWritesTheRegistration()
    {
        var startup = new FakeStartupRegistration();
        var chrome = new WidgetChromeViewModel();
        chrome.UseStartup(startup);

        chrome.ToggleStartWithWindows();

        startup.Enabled.Should().Be(1);
        startup.IsEnabled.Should().BeTrue();
        chrome.StartsWithWindows.Should().BeTrue();
    }

    [Fact]
    public void TurningItOffRemovesTheRegistration()
    {
        var startup = new FakeStartupRegistration { IsEnabled = true };
        var chrome = new WidgetChromeViewModel();
        chrome.UseStartup(startup);

        chrome.ToggleStartWithWindows();

        startup.Disabled.Should().Be(1);
        chrome.StartsWithWindows.Should().BeFalse();
    }

    [Fact]
    public void AWriteThatFails_LeavesTheTickTellingTheTruth()
    {
        // Registro travado por política é o caso real. Um visto marcado sobre
        // uma escrita que não aconteceu prometeria ao usuário um app que não
        // vai subir no próximo login.
        var startup = new FakeStartupRegistration { Refuses = true };
        var chrome = new WidgetChromeViewModel();
        chrome.UseStartup(startup);

        chrome.ToggleStartWithWindows();

        startup.Enabled.Should().Be(1);
        chrome.StartsWithWindows.Should().BeFalse();
    }

    [Fact]
    public void OutsideWindows_TheOptionStaysHidden()
    {
        var startup = new FakeStartupRegistration { IsSupported = false };
        var chrome = new WidgetChromeViewModel();

        chrome.UseStartup(startup);

        chrome.CanStartWithWindows.Should().BeFalse();
    }

    [Fact]
    public void StartingWithWindows_NeverLeaksIntoTheWidgetFile()
    {
        // A regressão que importa: widget.json é uma segunda cópia esperando
        // para divergir da chave Run. "Abrir recolhido" mora lá; isto, não.
        var startup = new FakeStartupRegistration();
        var chrome = new WidgetChromeViewModel();
        chrome.UseStartup(startup);

        chrome.ToggleStartWithWindows();

        chrome.StartsWithWindows.Should().BeTrue();
        chrome.StartHidden.Should().BeFalse();
        chrome.CaptureInto(WidgetState.Default).Should().Be(WidgetState.Default);
    }

    [Fact]
    public void RestoringTheWidgetFile_DoesNotTouchTheStartupTick()
    {
        var startup = new FakeStartupRegistration { IsEnabled = true };
        var chrome = new WidgetChromeViewModel();
        chrome.UseStartup(startup);

        chrome.Restore(WidgetState.Default with { StartHidden = true });

        chrome.StartHidden.Should().BeTrue();
        chrome.StartsWithWindows.Should().BeTrue();
    }
}
