using Avalonia.Media;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// As cores do terminal do agente: as 16 do xterm num tom escuro, o cubo de
/// 216 e os 24 cinzas. Fixas, como as de qualquer terminal: o Claude escolhe
/// as cores pensando num fundo escuro.
/// </summary>
public static class TerminalPalette
{
    public static Color Foreground { get; } = Color.FromRgb(0xD4, 0xD4, 0xD4);

    public static Color Background { get; } = Color.FromRgb(0x1E, 0x1E, 0x1E);

    private static readonly Color[] Indexed = Build();

    /// <summary>A cor de uma célula; <paramref name="foreground"/> diz qual padrão usar.</summary>
    public static Color Resolve(TerminalColor color, bool foreground) => color.Kind switch
    {
        TerminalColorKind.Indexed => Indexed[color.Value & 0xFF],
        TerminalColorKind.Rgb => Color.FromRgb(color.Red, color.Green, color.Blue),
        _ => foreground ? Foreground : Background,
    };

    private static Color[] Build()
    {
        var colors = new Color[256];
        uint[] basic =
        [
            0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
            0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
        ];

        for (var i = 0; i < basic.Length; i++)
        {
            colors[i] = Color.FromRgb((byte)(basic[i] >> 16), (byte)(basic[i] >> 8), (byte)basic[i]);
        }

        byte Level(int step) => step == 0 ? (byte)0 : (byte)(55 + (step * 40));

        for (var i = 0; i < 216; i++)
        {
            colors[16 + i] = Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        for (var i = 0; i < 24; i++)
        {
            var gray = (byte)(8 + (i * 10));
            colors[232 + i] = Color.FromRgb(gray, gray, gray);
        }

        return colors;
    }
}
