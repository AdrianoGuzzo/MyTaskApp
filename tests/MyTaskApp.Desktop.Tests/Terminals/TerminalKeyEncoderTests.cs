using System.Text;
using Avalonia.Input;
using MyTaskApp.Desktop.Terminals;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>As teclas no que um xterm manda (ADR-098): é o que o Claude espera ler.</summary>
public sealed class TerminalKeyEncoderTests
{
    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, "\r")]
    [InlineData(Key.Enter, KeyModifiers.Shift, "\x1b\r")]
    [InlineData(Key.Back, KeyModifiers.None, "\x7f")]
    [InlineData(Key.Tab, KeyModifiers.None, "\t")]
    [InlineData(Key.Tab, KeyModifiers.Shift, "\x1b[Z")]
    [InlineData(Key.Escape, KeyModifiers.None, "\x1b")]
    [InlineData(Key.Up, KeyModifiers.None, "\x1b[A")]
    [InlineData(Key.Left, KeyModifiers.Control, "\x1b[1;5D")]
    [InlineData(Key.Right, KeyModifiers.Shift, "\x1b[1;2C")]
    [InlineData(Key.Delete, KeyModifiers.None, "\x1b[3~")]
    [InlineData(Key.PageUp, KeyModifiers.None, "\x1b[5~")]
    [InlineData(Key.F1, KeyModifiers.None, "\x1bOP")]
    [InlineData(Key.F5, KeyModifiers.None, "\x1b[15~")]
    [InlineData(Key.F12, KeyModifiers.Shift, "\x1b[24;2~")]
    [InlineData(Key.C, KeyModifiers.Control, "\x03")]
    [InlineData(Key.D, KeyModifiers.Control, "\x04")]
    [InlineData(Key.Space, KeyModifiers.Control, "\0")]
    [InlineData(Key.B, KeyModifiers.Alt, "\u001bb")]
    public void TheKey_IsWhatXtermSends(Key key, KeyModifiers modifiers, string expected)
    {
        TerminalKeyEncoder.Encode(key, modifiers, applicationCursorKeys: false).Should().Equal(Encoding.UTF8.GetBytes(expected));
    }

    [Fact]
    public void InApplicationMode_TheArrowsAreSs3()
    {
        TerminalKeyEncoder.Encode(Key.Up, KeyModifiers.None, applicationCursorKeys: true).Should().Equal("\x1bOA"u8.ToArray());
        TerminalKeyEncoder.Encode(Key.Up, KeyModifiers.Control, applicationCursorKeys: true)
            .Should().Equal("\x1b[1;5A"u8.ToArray(), "com modificador é CSI nos dois modos");
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt)]
    public void Text_ComesFromTextInput_NotFromHere(Key key, KeyModifiers modifiers)
    {
        // Ctrl+Alt é o AltGr do teclado ABNT: a barra e o ponto de interrogação vêm por ele.
        TerminalKeyEncoder.Encode(key, modifiers, applicationCursorKeys: false).Should().BeNull();
    }

    [Fact]
    public void APaste_IsBracketed_WhenTheClaudeAsked_AndCannotCloseItself()
    {
        var pasted = Encoding.UTF8.GetString(TerminalKeyEncoder.Paste("linha 1\r\nlinha 2\x1b[201~fim", bracketedPaste: true));

        pasted.Should().Be("\x1b[200~linha 1\rlinha 2fim\x1b[201~");
        Encoding.UTF8.GetString(TerminalKeyEncoder.Paste("a\nb", bracketedPaste: false)).Should().Be("a\rb");
    }
}
