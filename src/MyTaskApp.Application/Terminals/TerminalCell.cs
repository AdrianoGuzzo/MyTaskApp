namespace MyTaskApp.Application.Terminals;

/// <summary>Cor de uma célula: a padrão do tema, uma das 256 da paleta, ou RGB.</summary>
public readonly record struct TerminalColor(TerminalColorKind Kind, uint Value)
{
    public static TerminalColor Default { get; } = new(TerminalColorKind.Default, 0);

    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, (uint)Math.Clamp(index, 0, 255));

    public static TerminalColor Rgb(int red, int green, int blue) => new(
        TerminalColorKind.Rgb,
        ((uint)Math.Clamp(red, 0, 255) << 16) | ((uint)Math.Clamp(green, 0, 255) << 8) | (uint)Math.Clamp(blue, 0, 255));

    public bool IsDefault => Kind == TerminalColorKind.Default;

    public byte Red => (byte)(Value >> 16);

    public byte Green => (byte)(Value >> 8);

    public byte Blue => (byte)Value;
}

public enum TerminalColorKind : byte
{
    Default,
    Indexed,
    Rgb,
}

[Flags]
public enum TerminalCellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strikethrough = 128,
}

/// <summary>Como uma célula é pintada.</summary>
public readonly record struct TerminalStyle(TerminalColor Foreground, TerminalColor Background, TerminalCellFlags Flags)
{
    public static TerminalStyle Default { get; } = new(TerminalColor.Default, TerminalColor.Default, TerminalCellFlags.None);

    public bool IsDefault => this == Default;
}

/// <summary>
/// Uma posição da grade. <see cref="Text"/> é o grafema inteiro (um caractere,
/// ou um par substituto, mais as marcas que se combinam com ele). Um caractere
/// largo ocupa duas células: a primeira com <see cref="Width"/> 2 e a seguinte
/// com 0, que não se desenha.
/// </summary>
public readonly record struct TerminalCell(string Text, TerminalStyle Style, byte Width)
{
    public static TerminalCell Blank { get; } = new(" ", TerminalStyle.Default, 1);

    public static TerminalCell BlankWith(TerminalStyle style) =>
        new(" ", new TerminalStyle(TerminalColor.Default, style.Background, TerminalCellFlags.None), 1);

    public bool IsContinuation => Width == 0;

    /// <summary>Espaço sem cor de fundo: o que sobra no fim da linha não precisa ir num snapshot.</summary>
    public bool IsEmpty => Width == 1 && Text == " " && Style.Background.IsDefault
        && (Style.Flags & (TerminalCellFlags.Inverse | TerminalCellFlags.Underline | TerminalCellFlags.Strikethrough)) == 0;
}
