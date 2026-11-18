namespace MyTaskApp.Mobile.Core.ViewModels;

public static class TerminalScroll
{
    public static int MaximumOffset(int historyRows, int screenRows, int visibleRows) =>
        Math.Max(0, historyRows + screenRows - visibleRows);

    public static int AfterOutput(int offset, int maximum, long addedLines, bool sameScreen) =>
        sameScreen && offset > 0 ? (int)Math.Min(maximum, offset + Math.Max(0, addedLines)) : 0;
}
