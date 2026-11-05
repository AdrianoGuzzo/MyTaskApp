using System.Text;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>As teclas sem tela, as mesmas do desktop e do celular (ADR-101).</summary>
public sealed class TerminalKeyBytesTests
{
    [Theory]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None, false, "\r")]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.Shift, false, "\x1b\r")]
    [InlineData(TerminalKey.Tab, TerminalKeyModifiers.Shift, false, "\x1b[Z")]
    [InlineData(TerminalKey.Escape, TerminalKeyModifiers.None, false, "\x1b")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, false, "\x1b[A")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, true, "\x1bOA")]
    [InlineData(TerminalKey.Left, TerminalKeyModifiers.Control, true, "\x1b[1;5D")]
    [InlineData(TerminalKey.Backspace, TerminalKeyModifiers.None, false, "\x7f")]
    [InlineData(TerminalKey.PageDown, TerminalKeyModifiers.None, false, "\x1b[6~")]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.None, false, "\x1bOP")]
    [InlineData(TerminalKey.F12, TerminalKeyModifiers.Shift, false, "\x1b[24;2~")]
    public void TheNamedKeys_AreTheXtermOnes(TerminalKey key, TerminalKeyModifiers modifiers, bool applicationCursorKeys, string expected) =>
        Encoding.UTF8.GetString(TerminalKeyBytes.Encode(key, modifiers, applicationCursorKeys)).Should().Be(expected);

    [Theory]
    [InlineData('c', "\x03")]
    [InlineData('C', "\x03")]
    [InlineData('d', "\x04")]
    [InlineData(' ', "\0")]
    [InlineData('[', "\x1b")]
    [InlineData('-', "\x1f")]
    public void Control_IsTheControlCode(char character, string expected) =>
        Encoding.UTF8.GetString(TerminalKeyBytes.Control(character)!).Should().Be(expected);

    [Fact]
    public void Control_WithoutACode_IsNothing() => TerminalKeyBytes.Control('é').Should().BeNull();

    [Fact]
    public void Meta_PutsTheEscapeInFront()
    {
        Encoding.UTF8.GetString(TerminalKeyBytes.Meta('b')!).Should().Be("\u001bb");
        TerminalKeyBytes.Meta('/').Should().BeNull();
    }

    [Fact]
    public void Text_GoesAsUtf8_WithTheTerminalsEnter() =>
        Encoding.UTF8.GetString(TerminalKeyBytes.Text("olá\r\nmundo\n")).Should().Be("olá\rmundo\r");

    [Fact]
    public void Paste_IsBracketed_AndTheCloserInsideIsDropped() =>
        Encoding.UTF8.GetString(TerminalKeyBytes.Paste("a\x1b[201~b", bracketedPaste: true)).Should().Be("\x1b[200~ab\x1b[201~");
}
