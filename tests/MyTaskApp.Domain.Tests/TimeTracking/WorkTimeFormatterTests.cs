using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Domain.Tests.TimeTracking;

/// <summary>O texto do tempo trabalhado, escrito num lugar só (ADR-052).</summary>
public class WorkTimeFormatterTests
{
    [Theory]
    [InlineData(0, "0min")]
    [InlineData(0.5, "0min")]
    [InlineData(42, "42min")]
    [InlineData(60, "1h")]
    [InlineData(65, "1h 05min")]
    [InlineData(90, "1h 30min")]
    [InlineData(272, "4h 32min")]
    [InlineData(3120, "52h")]
    [InlineData(-5, "0min")]
    public void Duration_IsHoursAndMinutes(double minutes, string expected) =>
        WorkTimeFormatter.Duration(TimeSpan.FromMinutes(minutes)).Should().Be(expected);

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(1, "00:00:01")]
    [InlineData(2262, "00:37:42")]
    [InlineData(5862, "01:37:42")]
    [InlineData(446700, "124:05:00")]
    [InlineData(-3, "00:00:00")]
    public void Clock_IsTheRunningStopwatch(int seconds, string expected) =>
        WorkTimeFormatter.Clock(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData(270, 360, "4h 30min / 6h · 75%")]
    [InlineData(0, 360, "0min / 6h · 0%")]
    [InlineData(360, 360, "6h / 6h · 100%")]
    [InlineData(440, 360, "7h 20min / 6h · +1h 20min acima da estimativa")]
    public void AgainstEstimate_ShowsProgress_OrHowMuchItWentOver(int spent, int estimate, string expected) =>
        WorkTimeFormatter.AgainstEstimate(TimeSpan.FromMinutes(spent), TimeSpan.FromMinutes(estimate))
            .Should().Be(expected);

    [Fact]
    public void Range_IsWallClock_AndSaysTheDayWhenItCrossesMidnight()
    {
        var start = new DateTime(2026, 10, 6, 14, 0, 0);

        WorkTimeFormatter.Range(start, start.AddMinutes(90)).Should().Be("14:00 → 15:30");
        WorkTimeFormatter.Range(start.AddHours(9), start.AddHours(11.5)).Should().Be("23:00 → 07/10 01:30");
        WorkTimeFormatter.Range(start, null).Should().Be("14:00 → agora");
    }

    [Fact]
    public void Source_IsWhatTheHistoryShows()
    {
        WorkTimeFormatter.Source(TimeEntrySource.Timer).Should().Be("Timer");
        WorkTimeFormatter.Source(TimeEntrySource.Manual).Should().Be("Manual");
    }
}
