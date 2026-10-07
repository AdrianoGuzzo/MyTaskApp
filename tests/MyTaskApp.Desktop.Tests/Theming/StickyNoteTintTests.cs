using Avalonia.Media;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// A cor do post-it em todo tema (ADR-054): toda combinação de tema, cor e
/// destaque passa pelos mesmos mínimos que o ADR-041 impôs aos temas. Uma cor
/// de etiqueta qualquer não pode fazer o texto sumir.
/// </summary>
public class StickyNoteTintTests
{
    /// <summary>Sem cor, as 7 da paleta, as 12 de etiqueta e as duas pontas do espectro.</summary>
    private static readonly IReadOnlyList<string?> Hues =
    [
        null,
        .. StickyNotePalette.All.Select(StickyNotePalette.HexOf),
        .. TagColor.Palette,
        "#FFFFFF",
        "#000000",
    ];

    public static TheoryData<string, string?, bool> Combinations()
    {
        var data = new TheoryData<string, string?, bool>();

        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var hue in Hues)
            {
                data.Add(theme.Id, hue, false);
                data.Add(theme.Id, hue, true);
            }
        }

        return data;
    }

    private static StickyNoteAppearance For(string themeId, string? hue, bool attention) =>
        StickyNoteTint.For(ThemeCatalog.Find(themeId)!, hue is null ? null : Color.Parse(hue), attention);

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryTextLevel_StaysReadable(string themeId, string? hue, bool attention)
    {
        var palette = ThemeCatalog.Find(themeId)!.Palette;
        var look = For(themeId, hue, attention);

        foreach (var surface in new[] { look.Background, look.Header })
        {
            ColorContrast.Ratio(look.Text, surface).Should().BeGreaterThanOrEqualTo(StickyNoteTint.MainText);
            ColorContrast.Ratio(palette.TextMid, surface).Should().BeGreaterThanOrEqualTo(StickyNoteTint.SupportText);
            ColorContrast.Ratio(palette.TextLow, surface).Should().BeGreaterThanOrEqualTo(StickyNoteTint.SupportText);
        }

        look.Text.Should().Be(palette.TextHigh);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void TheAttentionStripe_IsAVisibleShape(string themeId, string? hue, bool attention)
    {
        var look = For(themeId, hue, attention);

        if (!attention)
        {
            look.Stripe.Should().BeNull();
            return;
        }

        ColorContrast.Ratio(look.Stripe!.Value, look.Background).Should().BeGreaterThanOrEqualTo(StickyNoteTint.Shapes);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void HighContrast_NeverTintsTheBackground(string themeId, string? hue, bool attention)
    {
        if (themeId != ThemeCatalog.HighContrast.Id)
        {
            return;
        }

        var palette = ThemeCatalog.HighContrast.Palette;
        var look = For(themeId, hue, attention);

        look.Background.Should().Be(palette.Surface);
        look.Header.Should().Be(palette.Surface);

        if (hue is not null)
        {
            look.BorderThickness.Should().BeGreaterThanOrEqualTo(2);
            ColorContrast.Ratio(look.Border, palette.Surface).Should().BeGreaterThanOrEqualTo(StickyNoteTint.Shapes);
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void WithoutColor_TheNoteIsTheThemeCard(string themeId)
    {
        var palette = ThemeCatalog.Find(themeId)!.Palette;

        var look = For(themeId, null, attention: false);

        look.Should().Be(new StickyNoteAppearance(palette.Surface, palette.Surface, palette.Stroke, 1, null, palette.TextHigh));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void AColor_ActuallyShows_OutsideHighContrast(string themeId)
    {
        var theme = ThemeCatalog.Find(themeId)!;

        if (StickyNoteTint.IsHighContrast(theme))
        {
            return;
        }

        var look = For(themeId, StickyNotePalette.HexOf(StickyNotePaletteColor.Blue), attention: false);

        look.Background.Should().NotBe(theme.Palette.Surface);
    }

    [Fact]
    public void Attention_IsStrongerThanNormal_WithTheSameColor()
    {
        var palette = ThemeCatalog.Charcoal.Palette;
        var hue = StickyNotePalette.HexOf(StickyNotePaletteColor.Orange);

        var normal = For(ThemeCatalog.Charcoal.Id, hue, attention: false);
        var attention = For(ThemeCatalog.Charcoal.Id, hue, attention: true);

        // Mesma claridade (a tinta preserva a luminância): o que cresce é a cor.
        Distance(attention.Background, palette.Surface)
            .Should().BeGreaterThan(Distance(normal.Background, palette.Surface));
        attention.Stripe.Should().NotBeNull();
    }

    /// <summary>
    /// A tinta tem de aparecer de verdade, e não só passar na régua: uma cor
    /// cortada até virar cinza passaria em todos os testes de contraste.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheTint_IsNoticeable(string themeId)
    {
        var theme = ThemeCatalog.Find(themeId)!;

        if (StickyNoteTint.IsHighContrast(theme))
        {
            return;
        }

        foreach (var color in StickyNotePalette.All.Where(color => color != StickyNotePaletteColor.Gray))
        {
            var look = For(themeId, StickyNotePalette.HexOf(color), attention: false);

            Distance(look.Background, theme.Palette.Surface).Should().BeGreaterThanOrEqualTo(12, $"{color} em {themeId}");
        }
    }

    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    [Fact]
    public void AttentionWithoutAColor_UsesTheThemesCautionAmber()
    {
        var palette = ThemeCatalog.Charcoal.Palette;

        var look = For(ThemeCatalog.Charcoal.Id, null, attention: true);

        look.Stripe.Should().Be(palette.Caution);
        look.Background.Should().NotBe(palette.Surface);
    }

    [Fact]
    public void Mixing_GoesFromOneColorToTheOther()
    {
        var black = Color.Parse("#000000");
        var white = Color.Parse("#FFFFFF");

        ColorContrast.Mix(black, white, 0).Should().Be(black);
        ColorContrast.Mix(black, white, 1).Should().Be(white);
        ColorContrast.Mix(black, white, 0.5).Should().Be(Color.FromRgb(128, 128, 128));
        ColorContrast.Mix(black, white, 7).Should().Be(white);
    }

    [Fact]
    public void MatchingLuminance_KeepsTheContrastWithText()
    {
        var card = Color.Parse("#1E2025");
        var orange = ColorContrast.Mix(card, Color.Parse("#F97316"), 0.3);

        var matched = ColorContrast.MatchLuminance(orange, card);

        ColorContrast.Luminance(matched).Should().BeApproximately(ColorContrast.Luminance(card), 0.002);
        ColorContrast.MatchLuminance(card, card).Should().Be(card);

        var paper = Color.Parse("#FFFFFF");
        var darkened = ColorContrast.MatchLuminance(Color.Parse("#808080"), paper);
        darkened.Should().Be(paper);
    }

    public static TheoryData<string> Themes() => [.. ThemeCatalog.All.Select(theme => theme.Id)];
}
