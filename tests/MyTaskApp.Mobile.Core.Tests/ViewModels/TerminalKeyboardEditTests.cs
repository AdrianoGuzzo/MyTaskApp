using System.Text;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

public sealed class TerminalKeyboardEditTests
{
    [Theory]
    [InlineData("", "o", "o")]
    [InlineData("o", "ol", "l")]
    [InlineData("ola", "ola ", " ")]
    [InlineData("ola ", "ola mundo", "mundo")]
    [InlineData("ola mundo", "ola mund", "\u007f")]
    [InlineData("ola mund", "ola mu", "\u007f\u007f")]
    [InlineData("cafe", "caf\u00e9", "\u007f\u00e9")]
    [InlineData("a", "a\u0301", "\u007fa\u0301")]
    [InlineData("a\U0001f469\u200d\U0001f4bb", "a", "\u007f")]
    [InlineData("oi", "oi\n", "\r")]
    public void EncodesOnlyTheEditedSuffix(string before, string after, string expected)
    {
        var marker = TerminalKeyboardEdit.Marker;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), TerminalKeyboardEdit.Encode(marker + before, marker + after));
    }

    [Fact]
    public void DeletingTheMarkerSendsBackspaceEveryTime()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(new byte[] { 0x7f }, TerminalKeyboardEdit.Encode(TerminalKeyboardEdit.Marker, ""));
        }
    }
}
