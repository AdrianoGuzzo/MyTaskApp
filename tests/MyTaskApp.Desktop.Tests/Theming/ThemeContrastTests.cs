using Avalonia.Media;
using MyTaskApp.Desktop.Theming;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// Todo tema do catálogo tem que ser legível (ADR-041). Os mínimos são os da
/// WCAG 2.2: 4,5:1 para texto (1.4.3), 3:1 para o que é só forma — ícone,
/// barra de progresso, bolinha (1.4.11). O texto principal pede 7:1 (1.4.6,
/// AAA): é ele que o usuário lê o dia inteiro.
/// </summary>
/// <remarks>
/// Um tema novo que não passe aqui não entra — nem com a desculpa de que "é
/// assim no tema original". O Carvão mudou dois tons para passar.
/// </remarks>
public class ThemeContrastTests
{
    private const double BodyText = 4.5;
    private const double MainText = 7;
    private const double Shapes = 3;

    public static TheoryData<string> Themes() => [.. ThemeCatalog.All.Select(theme => theme.Id)];

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheMainText_ReachesAaa_OnEverySurface(string id)
    {
        var p = Palette(id);

        ShouldContrast(p.TextHigh, p.Canvas, MainText);
        ShouldContrast(p.TextHigh, p.Surface, MainText);
        ShouldContrast(p.TextHigh, p.SurfaceHover, MainText);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheSecondaryTexts_AreStillReadable(string id)
    {
        // Inclusive o nível mais baixo: é nele que mora a data de 10px.
        var p = Palette(id);

        ShouldContrast(p.TextMid, p.Canvas, BodyText);
        ShouldContrast(p.TextMid, p.Surface, BodyText);
        ShouldContrast(p.TextMid, p.SurfaceHover, BodyText);
        ShouldContrast(p.TextLow, p.Canvas, BodyText);
        ShouldContrast(p.TextLow, p.Surface, BodyText);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheAccent_ReadsAsTextAndAsShape(string id)
    {
        var p = Palette(id);

        ShouldContrast(p.AccentText, p.Canvas, BodyText);
        ShouldContrast(p.AccentText, p.Surface, BodyText);
        ShouldContrast(p.AccentText, p.AccentSoft, BodyText);
        ShouldContrast(p.Accent, p.Canvas, Shapes);
        ShouldContrast(p.Accent, p.Surface, Shapes);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheLabelOnTheAccentButton_IsReadableInEveryState(string id)
    {
        var p = Palette(id);

        ShouldContrast(p.OnAccent, p.Accent, BodyText);
        ShouldContrast(p.OnAccent, p.AccentHover, BodyText);
        ShouldContrast(p.OnAccent, p.AccentPressed, BodyText);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheSignals_AreReadableAsText(string id)
    {
        // "Atrasada", "há alterações locais", o link da anotação: são texto,
        // não enfeite.
        var p = Palette(id);

        foreach (var signal in new[] { p.Danger, p.Caution, p.Info, p.Success })
        {
            ShouldContrast(signal, p.Canvas, BodyText);
            ShouldContrast(signal, p.Surface, BodyText);
        }

        ShouldContrast(p.Danger, p.DangerSoft, BodyText);
        ShouldContrast(p.Caution, p.CautionSoft, BodyText);
        ShouldContrast(p.TextHigh, p.DangerSoft, MainText);
        ShouldContrast(p.TextHigh, p.CautionSoft, MainText);
        ShouldContrast(p.TextHigh, p.AccentSoft, MainText);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheDestructiveButton_OnHover_IsReadable(string id)
    {
        // Button.danger:pointerover: fundo de perigo, texto na cor do fundo do painel.
        var p = Palette(id);

        ShouldContrast(p.Canvas, p.Danger, BodyText);
    }

    [Fact]
    public void TheHighContrastTheme_DrawsBordersYouCanSee()
    {
        // Nos outros temas a borda é sugestão; aqui ela delimita o controle.
        var p = ThemeCatalog.HighContrast.Palette;

        ShouldContrast(p.Stroke, p.Canvas, Shapes);
        ShouldContrast(p.StrokeSoft, p.Canvas, Shapes);
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000", 21)]
    [InlineData("#000000", "#000000", 1)]
    [InlineData("#767676", "#FFFFFF", 4.54)]
    public void TheRatio_MatchesTheWcagFormula(string a, string b, double expected)
    {
        Contrast.Ratio(Color.Parse(a), Color.Parse(b)).Should().BeApproximately(expected, 0.01);
    }

    private static ThemePalette Palette(string id) => ThemeCatalog.Find(id)!.Palette;

    private static void ShouldContrast(Color foreground, Color background, double minimum)
    {
        var ratio = Contrast.Ratio(foreground, background);

        ratio.Should().BeGreaterThanOrEqualTo(
            minimum,
            $"{foreground} sobre {background} dá {ratio:0.00}:1");
    }
}

/// <summary>Razão de contraste da WCAG 2.x, a partir da luminância relativa.</summary>
internal static class Contrast
{
    public static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;

        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
