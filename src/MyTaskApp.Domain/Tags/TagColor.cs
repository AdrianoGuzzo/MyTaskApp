using System.Globalization;

namespace MyTaskApp.Domain.Tags;

/// <summary>
/// A cor de uma etiqueta, sempre guardada como <c>#RRGGBB</c>. Uma forma só no
/// banco é o que deixa a mesma etiqueta ter a mesma cor em toda tela.
/// </summary>
public static class TagColor
{
    public const int HexLength = 7;

    /// <summary>
    /// As cores de um clique. Escolhidas para se destacar sobre o fundo escuro do
    /// widget sem brigar com o acento índigo da interface.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#EF4444", // vermelho
        "#F97316", // laranja
        "#F59E0B", // âmbar
        "#EAB308", // amarelo
        "#84CC16", // lima
        "#22C55E", // verde
        "#14B8A6", // turquesa
        "#06B6D4", // ciano
        "#3B82F6", // azul
        "#8B5CF6", // violeta
        "#EC4899", // rosa
        "#94A3B8", // cinza
    ];

    /// <summary>
    /// Aceita <c>#RGB</c> e <c>#RRGGBB</c>, com ou sem <c>#</c>, e devolve
    /// <c>#RRGGBB</c> em maiúsculas. Alfa é recusado: a bolinha sobre fundo
    /// escuro sumiria.
    /// </summary>
    public static string Normalize(string? hex)
    {
        var value = hex?.Trim().TrimStart('#') ?? string.Empty;

        if (value.Length == 3)
        {
            value = string.Concat(value.Select(digit => new string(digit, 2)));
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            throw new DomainException("A cor da etiqueta precisa estar no formato #RRGGBB.");
        }

        return "#" + value.ToUpperInvariant();
    }

    /// <summary>
    /// Texto escuro sobre a cor quando ele contrasta mais que o branco — o
    /// critério de contraste do WCAG, e não um limiar de brilho no olho.
    /// </summary>
    public static bool PrefersDarkText(string hex)
    {
        var normalized = Normalize(hex);

        var luminance =
            0.2126 * Channel(normalized, 1)
            + 0.7152 * Channel(normalized, 3)
            + 0.0722 * Channel(normalized, 5);

        // Contraste contra preto (L+0.05)/0.05 vence o contra branco
        // 1.05/(L+0.05) a partir de L ≈ 0.179.
        return (luminance + 0.05) / 0.05 > 1.05 / (luminance + 0.05);
    }

    private static double Channel(string hex, int start)
    {
        var srgb = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }
}
