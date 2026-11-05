using Avalonia.Input;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// Uma tecla do Avalonia nos bytes que um terminal xterm mandaria (ADR-098).
/// O texto comum chega pelo <c>TextInput</c>; aqui ficam as teclas que não
/// viram texto: Enter, setas, Ctrl+letra, F1–F12. As tabelas são as de
/// <see cref="TerminalKeyBytes"/>, as mesmas do celular (ADR-101).
/// </summary>
public static class TerminalKeyEncoder
{
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

        if (Named(key) is { } named)
        {
            var neutral = (shift ? TerminalKeyModifiers.Shift : TerminalKeyModifiers.None)
                | (alt ? TerminalKeyModifiers.Alt : TerminalKeyModifiers.None)
                | (control ? TerminalKeyModifiers.Control : TerminalKeyModifiers.None);

            return TerminalKeyBytes.Encode(named, neutral, applicationCursorKeys);
        }

        if (control && !alt)
        {
            return Character(key, shift: false) is { } character ? TerminalKeyBytes.Control(character) : null;
        }

        if (alt && !control)
        {
            return Character(key, shift) is { } character && char.IsAsciiLetterOrDigit(character) ? TerminalKeyBytes.Meta(character) : null;
        }

        return null;
    }

    /// <summary>Texto colado: entre marcadores quando o Claude pediu (<c>?2004h</c>), para ele não executar linha a linha.</summary>
    public static byte[] Paste(string text, bool bracketedPaste) => TerminalKeyBytes.Paste(text, bracketedPaste);

    private static TerminalKey? Named(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Right => TerminalKey.Right,
        Key.Left => TerminalKey.Left,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    /// <summary>A letra, o dígito ou o símbolo da tecla, para Ctrl e Alt.</summary>
    private static char? Character(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.OemPipe or Key.OemBackslash => '\\',
        Key.OemCloseBrackets => ']',
        Key.OemMinus => '-',
        _ => null,
    };
}
