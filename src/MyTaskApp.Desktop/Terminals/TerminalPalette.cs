using Avalonia.Media;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>As cores de <see cref="TerminalColors"/> como cores do Avalonia.</summary>
public static class TerminalPalette
{
    public static Color Foreground { get; } = From(TerminalColors.Foreground);

    public static Color Background { get; } = From(TerminalColors.Background);

    /// <summary>A cor de uma célula; <paramref name="foreground"/> diz qual padrão usar.</summary>
    public static Color Resolve(TerminalColor color, bool foreground) => From(TerminalColors.Resolve(color, foreground));

    private static Color From(TerminalRgb rgb) => Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue);
}
