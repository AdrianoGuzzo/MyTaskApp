using System.Text;
using Avalonia.Input;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// Uma tecla do Avalonia nos bytes que um terminal xterm mandaria (ADR-098).
/// O texto comum chega pelo <c>TextInput</c>; aqui ficam as teclas que não
/// viram texto: Enter, setas, Ctrl+letra, F1–F12.
/// </summary>
public static class TerminalKeyEncoder
{
    private const string Escape = "\x1b";

    /// <summary>
    /// Os bytes da tecla, ou <c>null</c> quando ela é texto (o <c>TextInput</c>
    /// manda) ou não tem equivalente no terminal.
    /// </summary>
    /// <param name="applicationCursorKeys">O modo <c>?1h</c> da tela: as setas viram <c>ESC O</c>.</param>
    public static byte[]? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var control = modifiers.HasFlag(KeyModifiers.Control);

        // O parâmetro de modificador do xterm: 1 + Shift + 2·Alt + 4·Ctrl.
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

        var sequence = key switch
        {
            // Shift+Enter quebra a linha no Claude, como Alt+Enter (o Meta-Enter dos terminais).
            Key.Enter when shift || alt => Escape + "\r",
            Key.Enter => "\r",
            Key.Back when control => "\x08",
            Key.Back => alt ? Escape + "\x7f" : "\x7f",
            Key.Tab when shift => Escape + "[Z",
            Key.Tab => "\t",
            Key.Escape => Escape,
            Key.Up => Cursor('A', modifier, applicationCursorKeys),
            Key.Down => Cursor('B', modifier, applicationCursorKeys),
            Key.Right => Cursor('C', modifier, applicationCursorKeys),
            Key.Left => Cursor('D', modifier, applicationCursorKeys),
            Key.Home => Cursor('H', modifier, applicationCursorKeys),
            Key.End => Cursor('F', modifier, applicationCursorKeys),
            Key.Insert => Tilde(2, modifier),
            Key.Delete => Tilde(3, modifier),
            Key.PageUp => Tilde(5, modifier),
            Key.PageDown => Tilde(6, modifier),
            Key.F1 => Function('P', modifier),
            Key.F2 => Function('Q', modifier),
            Key.F3 => Function('R', modifier),
            Key.F4 => Function('S', modifier),
            Key.F5 => Tilde(15, modifier),
            Key.F6 => Tilde(17, modifier),
            Key.F7 => Tilde(18, modifier),
            Key.F8 => Tilde(19, modifier),
            Key.F9 => Tilde(20, modifier),
            Key.F10 => Tilde(21, modifier),
            Key.F11 => Tilde(23, modifier),
            Key.F12 => Tilde(24, modifier),
            _ when control && !alt => Control(key),
            _ when alt && !control => Meta(key, shift),
            _ => null,
        };

        return sequence is null ? null : Encoding.UTF8.GetBytes(sequence);
    }

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

    /// <summary>Ctrl+letra é o código de controle dela; Ctrl+C é o 0x03 que o Claude lê como interromper.</summary>
    private static string? Control(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(),
        Key.Space or Key.D2 => "\0",
        Key.OemOpenBrackets or Key.D3 => Escape,
        Key.OemPipe or Key.OemBackslash or Key.D4 => "\x1c",
        Key.OemCloseBrackets or Key.D5 => "\x1d",
        Key.D6 => "\x1e",
        Key.OemMinus or Key.D7 => "\x1f",
        _ => null,
    };

    /// <summary>Alt+letra: o <c>ESC</c> na frente, como os terminais mandam o Meta.</summary>
    private static string? Meta(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => Escape + (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => Escape + (char)('0' + (key - Key.D0)),
        _ => null,
    };
}
