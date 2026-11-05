using System.Text;

namespace MyTaskApp.Application.Terminals;

/// <summary>As teclas que não viram texto, sem o tipo de nenhuma tela (ADR-098, ADR-101).</summary>
public enum TerminalKey
{
    Enter,
    Backspace,
    Tab,
    Escape,
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    Insert,
    Delete,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>
/// Uma tecla nos bytes que um terminal xterm mandaria. O desktop (Avalonia) e o
/// celular (MAUI) traduzem as teclas deles para cá, e o texto comum vai como
/// UTF-8.
/// </summary>
public static class TerminalKeyBytes
{
    private const string Escape = "\x1b";

    /// <summary>Os bytes da tecla.</summary>
    /// <param name="applicationCursorKeys">O modo <c>?1h</c> da tela: as setas viram <c>ESC O</c>.</param>
    public static byte[] Encode(TerminalKey key, TerminalKeyModifiers modifiers = TerminalKeyModifiers.None, bool applicationCursorKeys = false)
    {
        var shift = modifiers.HasFlag(TerminalKeyModifiers.Shift);
        var alt = modifiers.HasFlag(TerminalKeyModifiers.Alt);
        var control = modifiers.HasFlag(TerminalKeyModifiers.Control);

        // O parâmetro de modificador do xterm: 1 + Shift + 2·Alt + 4·Ctrl.
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

        var sequence = key switch
        {
            // Shift+Enter quebra a linha no Claude, como Alt+Enter (o Meta-Enter dos terminais).
            TerminalKey.Enter when shift || alt => Escape + "\r",
            TerminalKey.Enter => "\r",
            TerminalKey.Backspace when control => "\x08",
            TerminalKey.Backspace => alt ? Escape + "\x7f" : "\x7f",
            TerminalKey.Tab when shift => Escape + "[Z",
            TerminalKey.Tab => "\t",
            TerminalKey.Escape => Escape,
            TerminalKey.Up => Cursor('A', modifier, applicationCursorKeys),
            TerminalKey.Down => Cursor('B', modifier, applicationCursorKeys),
            TerminalKey.Right => Cursor('C', modifier, applicationCursorKeys),
            TerminalKey.Left => Cursor('D', modifier, applicationCursorKeys),
            TerminalKey.Home => Cursor('H', modifier, applicationCursorKeys),
            TerminalKey.End => Cursor('F', modifier, applicationCursorKeys),
            TerminalKey.Insert => Tilde(2, modifier),
            TerminalKey.Delete => Tilde(3, modifier),
            TerminalKey.PageUp => Tilde(5, modifier),
            TerminalKey.PageDown => Tilde(6, modifier),
            TerminalKey.F1 => Function('P', modifier),
            TerminalKey.F2 => Function('Q', modifier),
            TerminalKey.F3 => Function('R', modifier),
            TerminalKey.F4 => Function('S', modifier),
            TerminalKey.F5 => Tilde(15, modifier),
            TerminalKey.F6 => Tilde(17, modifier),
            TerminalKey.F7 => Tilde(18, modifier),
            TerminalKey.F8 => Tilde(19, modifier),
            TerminalKey.F9 => Tilde(20, modifier),
            TerminalKey.F10 => Tilde(21, modifier),
            TerminalKey.F11 => Tilde(23, modifier),
            TerminalKey.F12 => Tilde(24, modifier),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };

        return Encoding.UTF8.GetBytes(sequence);
    }

    /// <summary>
    /// Ctrl com uma letra ou um símbolo: o código de controle dele (Ctrl+C é o
    /// 0x03 que o Claude lê como interromper); <c>null</c> se não há.
    /// </summary>
    public static byte[]? Control(char character)
    {
        var sequence = char.ToLowerInvariant(character) switch
        {
            >= 'a' and <= 'z' and var letter => ((char)(letter - 'a' + 1)).ToString(),
            ' ' or '2' => "\0",
            '[' or '3' => Escape,
            '\\' or '|' or '4' => "\x1c",
            ']' or '5' => "\x1d",
            '6' => "\x1e",
            '-' or '7' => "\x1f",
            _ => null,
        };

        return sequence is null ? null : Encoding.UTF8.GetBytes(sequence);
    }

    /// <summary>Alt com uma letra ou um dígito: o <c>ESC</c> na frente, como os terminais mandam o Meta.</summary>
    public static byte[]? Meta(char character) =>
        char.IsAsciiLetterOrDigit(character) ? Encoding.UTF8.GetBytes(Escape + character) : null;

    /// <summary>Texto digitado: como está, com o Enter do sistema virando o do terminal.</summary>
    public static byte[] Text(string text) =>
        Encoding.UTF8.GetBytes(text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));

    /// <summary>Texto colado: entre marcadores quando o Claude pediu (<c>?2004h</c>), para ele não executar linha a linha.</summary>
    public static byte[] Paste(string text, bool bracketedPaste)
    {
        // O Enter do Windows vira o do terminal; os marcadores dentro do texto não fecham a colagem antes.
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');

        return Encoding.UTF8.GetBytes(bracketedPaste
            ? Escape + "[200~" + normalized.Replace(Escape + "[201~", string.Empty, StringComparison.Ordinal) + Escape + "[201~"
            : normalized);
    }

    private static string Cursor(char final, int modifier, bool applicationCursorKeys) =>
        modifier > 1
            ? $"{Escape}[1;{modifier}{final}"
            : applicationCursorKeys ? $"{Escape}O{final}" : $"{Escape}[{final}";

    private static string Tilde(int code, int modifier) =>
        modifier > 1 ? $"{Escape}[{code};{modifier}~" : $"{Escape}[{code}~";

    private static string Function(char final, int modifier) =>
        modifier > 1 ? $"{Escape}[1;{modifier}{final}" : $"{Escape}O{final}";
}
