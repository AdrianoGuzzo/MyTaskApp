using Avalonia.Media;

namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// Razão de contraste da WCAG 2.x, a partir da luminância relativa. Morava só
/// nos testes de tema (ADR-041); os post-its (ADR-054) passaram a calcular cor
/// na tela, e a conta que os testes conferem tem de ser a mesma que a tela faz.
/// </summary>
public static class ColorContrast
{
    public static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// <paramref name="from"/> misturada a <paramref name="to"/> na proporção
    /// <paramref name="amount"/> (0 = só a primeira, 1 = só a segunda). Opaca.
    /// </summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);

        return Color.FromRgb(Channel(from.R, to.R, t), Channel(from.G, to.G, t), Channel(from.B, to.B, t));
    }

    /// <summary>
    /// A mesma cor, clareada ou escurecida até a luminância de
    /// <paramref name="reference"/>: muda o matiz sem mudar o quanto ela
    /// contrasta com o texto.
    /// </summary>
    public static Color MatchLuminance(Color color, Color reference) =>
        WithLuminance(color, Luminance(reference));

    /// <summary>A mesma cor, clareada ou escurecida até a luminância relativa <paramref name="target"/>.</summary>
    public static Color WithLuminance(Color color, double target)
    {
        var current = Luminance(color);

        if (Math.Abs(current - target) < 1e-4)
        {
            return color;
        }

        // Mais clara que o alvo escurece rumo ao preto; mais escura clareia
        // rumo ao branco. A luminância é monotônica na mistura: busca binária.
        var toward = current > target ? Colors.Black : Colors.White;
        var (low, high) = (0d, 1d);

        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            var luminance = Luminance(Mix(color, toward, middle));

            if (current > target ? luminance > target : luminance < target)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return Mix(color, toward, high);
    }

    public static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static byte Channel(byte from, byte to, double t) =>
        (byte)Math.Round(from + ((to - from) * t));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;

        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
