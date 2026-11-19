using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

public sealed class TerminalScrollTests
{
    [Theory]
    [InlineData(100, 30, 30, 100)]
    [InlineData(0, 30, 12, 18)]
    [InlineData(100, 30, 12, 118)]
    [InlineData(0, 30, 32, 0)]
    public void IncludesHistoryAndClippedScreenRows(int history, int screen, int visible, int expected)
    {
        Assert.Equal(expected, TerminalScroll.MaximumOffset(history, screen, visible));
    }

    [Theory]
    [InlineData(0, 100, 4, true, 0)]
    [InlineData(10, 100, 4, true, 14)]
    [InlineData(99, 100, 4, true, 100)]
    [InlineData(10, 0, 0, true, 0)]
    [InlineData(10, 100, 4, false, 0)]
    public void AnchorsHistoryUntilTheScreenIsReplaced(int offset, int maximum, int added, bool sameScreen, int expected)
    {
        Assert.Equal(expected, TerminalScroll.AfterOutput(offset, maximum, added, sameScreen));
    }
}
