using MyTaskApp.Desktop.Theming;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// Quais temas existem e qual deles vai para a tela (ADR-040). Sem janela: a
/// decisão é pura, só a aplicação precisa do Avalonia.
/// </summary>
public class ThemeCatalogTests
{
    private static readonly SystemAppearance DarkWindows = new(IsDark: true, HighContrast: false);
    private static readonly SystemAppearance LightWindows = new(IsDark: false, HighContrast: false);
    private static readonly SystemAppearance ContrastWindows = new(IsDark: true, HighContrast: true);

    [Fact]
    public void ThereAreAtLeastFiveThemes_WithLightAndDarkOnes()
    {
        ThemeCatalog.All.Should().HaveCountGreaterThanOrEqualTo(5);
        ThemeCatalog.All.Should().Contain(theme => !theme.IsDark);
        ThemeCatalog.All.Should().Contain(theme => theme.IsDark);
    }

    [Fact]
    public void EveryThemeHasItsOwnIdAndName()
    {
        // O id vai para o widget.json: dois temas com o mesmo id trocariam a
        // escolha de alguém em silêncio.
        ThemeCatalog.All.Select(theme => theme.Id).Should().OnlyHaveUniqueItems();
        ThemeCatalog.All.Select(theme => theme.Name).Should().OnlyHaveUniqueItems();
        ThemeCatalog.All.Should().NotContain(theme => theme.Id == ThemeCatalog.SystemId);
        ThemeCatalog.All.Should().OnlyContain(theme => !string.IsNullOrWhiteSpace(theme.Description));
    }

    [Fact]
    public void FollowingWindows_InDarkMode_GivesCharcoal()
    {
        ThemeCatalog.Resolve(ThemeCatalog.SystemId, DarkWindows).Should().BeSameAs(ThemeCatalog.Charcoal);
    }

    [Fact]
    public void FollowingWindows_InLightMode_GivesPaper()
    {
        ThemeCatalog.Resolve(ThemeCatalog.SystemId, LightWindows).Should().BeSameAs(ThemeCatalog.Paper);
    }

    [Fact]
    public void FollowingWindows_WithHighContrastOn_GivesHighContrast()
    {
        ThemeCatalog.Resolve(ThemeCatalog.SystemId, ContrastWindows).Should().BeSameAs(ThemeCatalog.HighContrast);
        ThemeCatalog.Resolve(ThemeCatalog.SystemId, ContrastWindows with { IsDark = false })
            .Should().BeSameAs(ThemeCatalog.HighContrast);
    }

    [Fact]
    public void AChosenTheme_WinsOverWhateverWindowsSays()
    {
        // Quem escolheu viu como fica: nem o modo escuro nem o alto contraste
        // do sistema desfazem a escolha.
        ThemeCatalog.Resolve("sepia", DarkWindows).Should().BeSameAs(ThemeCatalog.Sepia);
        ThemeCatalog.Resolve("nordic", LightWindows).Should().BeSameAs(ThemeCatalog.Nordic);
        ThemeCatalog.Resolve("paper", ContrastWindows).Should().BeSameAs(ThemeCatalog.Paper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tema-que-saiu")]
    [InlineData("Charcoal")]
    public void AnUnknownChoice_FallsBackToFollowingWindows(string? choice)
    {
        ThemeCatalog.Normalize(choice).Should().Be(ThemeCatalog.SystemId);
        ThemeCatalog.Resolve(choice, LightWindows).Should().BeSameAs(ThemeCatalog.Paper);
    }

    [Fact]
    public void AKnownChoice_IsKept()
    {
        ThemeCatalog.Normalize("plum").Should().Be("plum");
        ThemeCatalog.Normalize(ThemeCatalog.SystemId).Should().Be(ThemeCatalog.SystemId);
    }
}
