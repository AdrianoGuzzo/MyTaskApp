using Avalonia.Media;

namespace MyTaskApp.Desktop.Theming;

/// <summary>As cores de um post-it no tema ativo. Opacas: a opacidade escolhida entra no pincel.</summary>
/// <param name="Stripe">A faixa lateral do modo Atenção; nula no Normal.</param>
public sealed record StickyNoteAppearance(
    Color Background,
    Color Header,
    Color Border,
    double BorderThickness,
    Color? Stripe,
    Color Text);

/// <summary>
/// Como a cor de um post-it vira desenho sem quebrar o tema (ADR-054). A cor é
/// <b>tinta sobre o cartão do tema</b>, e não fundo chapado: um post-it "amarelo"
/// no Carvão é um cartão escuro puxado para o âmbar, não um retângulo amarelo
/// com letra clara por cima.
/// </summary>
/// <remarks>
/// <para>
/// O texto é sempre o do tema. Quem cede é a tinta: se ela tirar qualquer um
/// dos três níveis de texto do mínimo da WCAG que o ADR-041 fixou (7:1 para o
/// principal, 4,5:1 para os de apoio), ela diminui até eles voltarem. Uma cor
/// de etiqueta qualquer, escolhida no <c>ColorView</c>, passa pela mesma régua.
/// </para>
/// <para>
/// No Alto contraste não há tinta nenhuma: o fundo é o cartão do tema, e a cor
/// vira borda de 2px. Fundo colorido é justamente o que esse tema existe para
/// evitar.
/// </para>
/// </remarks>
public static class StickyNoteTint
{
    public const double MainText = 7;
    public const double SupportText = 4.5;
    public const double Shapes = 3;

    /// <summary>Quanto da matiz entra no cartão. O claro aguenta mais tinta antes de a letra perder contraste.</summary>
    public const double DarkStrength = 0.16;

    public const double LightStrength = 0.24;

    /// <summary>O que o modo Atenção soma à tinta: mais cor, sem piscar nada.</summary>
    public const double AttentionBoost = 0.12;

    /// <summary>O cabeçalho é um tom acima do corpo: separa sem precisar de linha.</summary>
    public const double HeaderBoost = 0.08;

    private const double Step = 0.02;

    /// <param name="hue">A matiz escolhida (paleta ou etiqueta); nula segue o tema.</param>
    public static StickyNoteAppearance For(AppTheme theme, Color? hue, bool attention)
    {
        var palette = theme.Palette;

        // Atenção sem cor própria usa o âmbar do tema: a semântica fixa do
        // ADR-041, em que âmbar é atenção em todo tema.
        var seed = hue ?? (attention ? palette.Caution : (Color?)null);

        if (seed is not { } color)
        {
            return new StickyNoteAppearance(palette.Surface, palette.Surface, palette.Stroke, 1, null, palette.TextHigh);
        }

        if (IsHighContrast(theme))
        {
            var line = Legible(color, palette.Surface, palette.TextHigh);

            return new StickyNoteAppearance(
                palette.Surface,
                palette.Surface,
                line,
                attention ? 3 : 2,
                attention ? line : null,
                palette.TextHigh);
        }

        var strength = (theme.IsDark ? DarkStrength : LightStrength) + (attention ? AttentionBoost : 0);
        var background = TintWithin(palette, color, strength);
        var header = TintWithin(palette, color, strength + HeaderBoost);

        return new StickyNoteAppearance(
            background,
            header,
            ColorContrast.Mix(palette.Stroke, color, 0.5),
            1,
            attention ? Legible(color, background, palette.TextHigh) : null,
            palette.TextHigh);
    }

    public static bool IsHighContrast(AppTheme theme) => theme.Id == ThemeCatalog.HighContrast.Id;

    /// <summary>
    /// A tinta mais forte, até <paramref name="strength"/>, em que os três níveis
    /// de texto continuam legíveis.
    /// </summary>
    /// <remarks>
    /// A mistura é trazida para dentro da faixa de claridade em que os três
    /// textos passam: muda a cor sem tirar a letra do mínimo. Sem isso, no
    /// escuro a tinta clareia o fundo, a letra clara perde contraste e a régua
    /// corta a tinta até quase nada — o post-it "laranja" no Carvão saía cinza.
    /// No claro é o contrário: a tinta escurece o branco, e é ela que é
    /// clareada de volta.
    /// </remarks>
    private static Color TintWithin(ThemePalette palette, Color hue, double strength)
    {
        var (lowest, highest) = LuminanceBand(palette);

        for (var amount = strength; amount > 0; amount -= Step)
        {
            var mixed = ColorContrast.Mix(palette.Surface, hue, amount);
            var luminance = ColorContrast.Luminance(mixed);

            var tinted = luminance > highest ? ColorContrast.WithLuminance(mixed, highest)
                : luminance < lowest ? ColorContrast.WithLuminance(mixed, lowest)
                : mixed;

            if (ColorContrast.Ratio(palette.TextHigh, tinted) >= MainText
                && ColorContrast.Ratio(palette.TextMid, tinted) >= SupportText
                && ColorContrast.Ratio(palette.TextLow, tinted) >= SupportText)
            {
                return tinted;
            }
        }

        return palette.Surface;
    }

    /// <summary>
    /// A faixa de luminância de fundo em que os três níveis de texto passam. É a
    /// fórmula da WCAG resolvida para o fundo: com letra clara, o fundo tem
    /// teto; com letra escura, piso. Uma folga pequena absorve o arredondamento
    /// para bytes.
    /// </summary>
    private static (double Lowest, double Highest) LuminanceBand(ThemePalette palette)
    {
        const double Slack = 0.002;

        var surface = ColorContrast.Luminance(palette.Surface);
        var (lowest, highest) = (0d, 1d);

        foreach (var (text, minimum) in new[]
                 {
                     (palette.TextHigh, MainText),
                     (palette.TextMid, SupportText),
                     (palette.TextLow, SupportText),
                 })
        {
            var luminance = ColorContrast.Luminance(text);

            if (luminance > surface)
            {
                highest = Math.Min(highest, ((luminance + 0.05) / minimum) - 0.05 - Slack);
            }
            else
            {
                lowest = Math.Max(lowest, (minimum * (luminance + 0.05)) - 0.05 + Slack);
            }
        }

        return (lowest, highest);
    }

    /// <summary>
    /// A matiz como forma (faixa, borda): puxada na direção do texto até se
    /// destacar 3:1 do fundo. Amarelo puro sobre o Papel some; um amarelo
    /// escurecido, não.
    /// </summary>
    private static Color Legible(Color hue, Color background, Color toward)
    {
        for (var amount = 0d; amount <= 1; amount += 0.05)
        {
            var candidate = ColorContrast.Mix(hue, toward, amount);

            if (ColorContrast.Ratio(candidate, background) >= Shapes)
            {
                return candidate;
            }
        }

        return toward;
    }
}
