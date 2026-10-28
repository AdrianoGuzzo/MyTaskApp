using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Domain.Tests.TimeTracking;

/// <summary>A repartição de um período pelos dias locais (ADR-097).</summary>
public class LocalDayGridTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-3);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, Offset);

    /// <summary>De 01/10 a 31/10, com as meias-noites no fuso fixo de -3h.</summary>
    private static LocalDayGrid October() =>
        new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Offset));

    [Fact]
    public void APeriodInsideOneDay_IsWhole()
    {
        October().Split(At(14, 9), At(14, 12, 30)).Should().Equal((13, TimeSpan.FromMinutes(210)));
    }

    [Fact]
    public void APeriodAcrossMidnight_CountsInEachDayWhatFitsInIt()
    {
        October().Split(At(14, 23), At(15, 1, 30)).Should().Equal(
            (13, TimeSpan.FromHours(1)),
            (14, TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void APeriodOfSeveralDays_FillsTheDaysInBetween()
    {
        October().Split(At(3, 18), At(6, 6)).Should().Equal(
            (2, TimeSpan.FromHours(6)),
            (3, TimeSpan.FromHours(24)),
            (4, TimeSpan.FromHours(24)),
            (5, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void WhatFallsOutsideTheGrid_IsCut()
    {
        var grid = October();

        grid.Split(new DateTimeOffset(2026, 9, 30, 22, 0, 0, Offset), At(1, 2)).Should().Equal((0, TimeSpan.FromHours(2)));
        grid.Split(At(31, 23), new DateTimeOffset(2026, 11, 1, 3, 0, 0, Offset)).Should().Equal((30, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void APeriodOutsideTheGrid_IsNothing()
    {
        var grid = October();

        grid.Split(new DateTimeOffset(2026, 9, 29, 8, 0, 0, Offset), new DateTimeOffset(2026, 9, 30, 8, 0, 0, Offset)).Should().BeEmpty();
        grid.Split(new DateTimeOffset(2026, 11, 1, 0, 0, 0, Offset), new DateTimeOffset(2026, 11, 1, 8, 0, 0, Offset)).Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyOrInvertedPeriod_IsNothing()
    {
        var grid = October();

        grid.Split(At(14, 9), At(14, 9)).Should().BeEmpty();
        grid.Split(At(14, 10), At(14, 9)).Should().BeEmpty();
    }

    [Fact]
    public void ThePeriodEndingAtMidnight_DoesNotTouchTheNextDay()
    {
        October().Split(At(14, 22), At(15, 0)).Should().Equal((13, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void TheDaysOfTheDaylightSavingChange_HaveTheirRealLength()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateTimeOffset StartOf(DateOnly date)
        {
            var local = date.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        // 08/03/2026 tem 23 horas; 01/11/2026, 25.
        var spring = new LocalDayGrid(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8), StartOf);
        var fall = new LocalDayGrid(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), StartOf);

        spring.Split(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Should().Equal((0, TimeSpan.FromHours(23)));
        fall.Split(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Should().Equal((0, TimeSpan.FromHours(25)));
    }

    [Fact]
    public void IndexOf_FindsTheDay_OrSaysItIsOutside()
    {
        var grid = October();

        grid.Count.Should().Be(31);
        grid.IndexOf(new DateOnly(2026, 10, 14)).Should().Be(13);
        grid.DateAt(13).Should().Be(new DateOnly(2026, 10, 14));
        grid.IndexOf(new DateOnly(2026, 9, 30)).Should().Be(-1);
        grid.IndexOf(new DateOnly(2026, 11, 1)).Should().Be(-1);
        grid.StartOf(31).Should().Be(new DateTimeOffset(2026, 11, 1, 0, 0, 0, Offset));
    }

    [Fact]
    public void TheLastDayBeforeTheFirst_IsRefused()
    {
        FluentActions.Invoking(() => new LocalDayGrid(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), _ => DateTimeOffset.UnixEpoch))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
