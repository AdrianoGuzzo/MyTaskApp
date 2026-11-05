namespace MyTaskApp.Application.Terminals;

/// <summary>Uma cor do terminal em RGB, sem o tipo de nenhuma tela.</summary>
public readonly record struct TerminalRgb(byte Red, byte Green, byte Blue);

/// <summary>
/// As cores do terminal do agente: as 16 do xterm num tom escuro, o cubo de
/// 216 e os 24 cinzas. Fixas, como as de qualquer terminal: o Claude escolhe
/// as cores pensando num fundo escuro. O desktop e o celular desenham com
/// elas (ADR-101).
/// </summary>
public static class TerminalColors
{
    public static TerminalRgb Foreground { get; } = new(0xD4, 0xD4, 0xD4);

    public static TerminalRgb Background { get; } = new(0x1E, 0x1E, 0x1E);

    private static readonly TerminalRgb[] Indexed = Build();

    /// <summary>A cor de uma célula; <paramref name="foreground"/> diz qual padrão usar.</summary>
    public static TerminalRgb Resolve(TerminalColor color, bool foreground) => color.Kind switch
    {
        TerminalColorKind.Indexed => Indexed[color.Value & 0xFF],
        TerminalColorKind.Rgb => new(color.Red, color.Green, color.Blue),
        _ => foreground ? Foreground : Background,
    };

    private static TerminalRgb[] Build()
    {
        var colors = new TerminalRgb[256];
        uint[] basic =
        [
            0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
            0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
        ];

        for (var i = 0; i < basic.Length; i++)
        {
            colors[i] = new((byte)(basic[i] >> 16), (byte)(basic[i] >> 8), (byte)basic[i]);
        }

        byte Level(int step) => step == 0 ? (byte)0 : (byte)(55 + (step * 40));

        for (var i = 0; i < 216; i++)
        {
            colors[16 + i] = new(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        for (var i = 0; i < 24; i++)
        {
            var gray = (byte)(8 + (i * 10));
            colors[232 + i] = new(gray, gray, gray);
        }

        return colors;
    }
}
