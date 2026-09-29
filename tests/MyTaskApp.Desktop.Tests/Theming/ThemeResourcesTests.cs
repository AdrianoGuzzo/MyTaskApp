using System.Text.RegularExpressions;
using Avalonia.Media;
using MyTaskApp.Desktop.Theming;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// O dicionário que cada tema põe na tela (ADR-040). A guarda que importa é a
/// última: uma tela que pede uma chave que tema nenhum define compila, abre, e
/// desenha sem cor — em silêncio.
/// </summary>
public partial class ThemeResourcesTests
{
    public static TheoryData<string> Themes() => [.. ThemeCatalog.All.Select(theme => theme.Id)];

    [Fact]
    public void EveryThemeDefinesTheSameKeys()
    {
        // Uma chave que só um tema define funciona nele e some nos outros.
        var reference = Keys(ThemeCatalog.All[0]);

        foreach (var theme in ThemeCatalog.All)
        {
            Keys(theme).Should().BeEquivalentTo(reference, $"o tema {theme.Id} precisa das mesmas chaves");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheBrushesCarryThePaletteColors(string id)
    {
        var theme = ThemeCatalog.Find(id)!;
        var resources = ThemeResources.Build(theme);

        resources["WidgetCanvasColor"].Should().Be(theme.Palette.Canvas);
        ((ISolidColorBrush)resources["WidgetCanvasBrush"]!).Color.Should().Be(theme.Palette.Canvas);
        ((ISolidColorBrush)resources["WidgetAccentTextBrush"]!).Color.Should().Be(theme.Palette.AccentText);
        ((ISolidColorBrush)resources["AccentFillColorDefaultBrush"]!).Color.Should().Be(theme.Palette.Accent);
        ((ISolidColorBrush)resources["TextFillColorPrimaryBrush"]!).Color.Should().Be(theme.Palette.TextHigh);
        ((ISolidColorBrush)resources["MenuFlyoutPresenterBackground"]!).Color.Should().Be(theme.Palette.Surface);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryKeyAScreenAsksFor_ExistsInTheTheme(string id)
    {
        var keys = Keys(ThemeCatalog.Find(id)!);

        var missing = RequestedWidgetKeys().Where(key => !keys.Contains(key)).ToList();

        missing.Should().BeEmpty("toda chave Widget* que uma tela pede precisa vir do tema");
    }

    [Fact]
    public void NoScreenFreezesAThemeColor()
    {
        // StaticResource resolve uma vez, na carga: a tela ficaria presa ao
        // tema que estava ativo quando abriu.
        var frozen = Screens()
            .SelectMany(file => StaticWidgetKey().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .Where(use => !ThemeIndependent.Any(use.Contains))
            .ToList();

        frozen.Should().BeEmpty();
    }

    [Fact]
    public void MixingGoesFromOneColorToTheOther()
    {
        var black = Colors.Black;
        var white = Colors.White;

        ThemeResources.Mix(black, white, 0).Should().Be(black);
        ThemeResources.Mix(black, white, 1).Should().Be(white);
        ThemeResources.Mix(black, white, 0.5).Should().Be(Color.FromRgb(128, 128, 128));
    }

    /// <summary>O que o Tokens.axaml define de propósito fora do tema.</summary>
    private static readonly string[] ThemeIndependent =
        ["WidgetPillRadius", "WidgetCardRadius", "WidgetShellRadius", "WidgetIconFont", "WidgetTransparentBrush"];

    private static HashSet<string> Keys(AppTheme theme) =>
        [.. ThemeResources.Build(theme).Keys.Cast<string>()];

    private static IEnumerable<string> RequestedWidgetKeys() =>
        Screens()
            .SelectMany(file => DynamicWidgetKey().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Where(key => !ThemeIndependent.Contains(key))
            .Distinct();

    private static IEnumerable<string> Screens() =>
        Directory.EnumerateFiles(DesktopSources(), "*.axaml", SearchOption.AllDirectories);

    /// <summary>Sobe do binário até o <c>.slnx</c>, como os testes de packaging.</summary>
    private static string DesktopSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyTaskApp.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("MyTaskApp.slnx não encontrado"),
            "src",
            "MyTaskApp.Desktop");
    }

    [GeneratedRegex(@"\{DynamicResource (Widget\w+)\}")]
    private static partial Regex DynamicWidgetKey();

    [GeneratedRegex(@"\{StaticResource Widget\w+\}")]
    private static partial Regex StaticWidgetKey();
}
