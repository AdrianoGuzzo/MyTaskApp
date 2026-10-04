using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class DeadlineShortcutsTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly Friday = new(2026, 10, 9);
    private static readonly DateOnly Saturday = new(2026, 10, 10);
    private static readonly TimeOnly SixPm = new(18, 0);
    private static readonly TimeOnly Morning = new(9, 0);

    private static TaskDeadline Resolve(DeadlineShortcut shortcut, DateOnly today, TimeOnly? now = null) =>
        DeadlineShortcuts.Resolve(shortcut, today, now ?? Morning, SixPm);

    [Fact]
    public void Today_KeepsTheTime()
    {
        Resolve(DeadlineShortcut.Today, Monday).Should().Be(new TaskDeadline(Monday, SixPm));
    }

    [Fact]
    public void Today_AfterTheTime_BecomesTheEndOfTheDay()
    {
        Resolve(DeadlineShortcut.Today, Monday, new TimeOnly(19, 0))
            .Should().Be(new TaskDeadline(Monday, DeadlineShortcuts.EndOfDay));
    }

    [Theory]
    [InlineData(DeadlineShortcut.Tomorrow, 1)]
    [InlineData(DeadlineShortcut.InThreeDays, 3)]
    [InlineData(DeadlineShortcut.InOneWeek, 7)]
    public void RelativeShortcuts_CountFromToday(DeadlineShortcut shortcut, int days)
    {
        Resolve(shortcut, Monday).Should().Be(new TaskDeadline(Monday.AddDays(days), SixPm));
    }

    [Fact]
    public void EndOfWeek_IsThisFriday()
    {
        Resolve(DeadlineShortcut.EndOfWeek, Monday).Date.Should().Be(Friday);
        Resolve(DeadlineShortcut.EndOfWeek, Friday).Date.Should().Be(Friday);
    }

    [Fact]
    public void EndOfWeek_OnFridayEvening_IsNextFriday()
    {
        Resolve(DeadlineShortcut.EndOfWeek, Friday, new TimeOnly(18, 30)).Date.Should().Be(Friday.AddDays(7));
    }

    [Fact]
    public void EndOfWeek_OnTheWeekend_IsTheComingFriday()
    {
        Resolve(DeadlineShortcut.EndOfWeek, Saturday).Date.Should().Be(Friday.AddDays(7));
        Resolve(DeadlineShortcut.EndOfWeek, Saturday.AddDays(1)).Date.Should().Be(Friday.AddDays(7));
    }

    [Fact]
    public void NextWeek_IsTheFridayOfTheFollowingWeek()
    {
        Resolve(DeadlineShortcut.NextWeek, Monday).Date.Should().Be(Friday.AddDays(7));
        Resolve(DeadlineShortcut.NextWeek, Friday).Date.Should().Be(Friday.AddDays(7));
    }

    [Fact]
    public void AnUnknownShortcut_IsRefused()
    {
        var resolve = () => Resolve((DeadlineShortcut)99, Monday);

        resolve.Should().Throw<DomainException>();
    }
}
