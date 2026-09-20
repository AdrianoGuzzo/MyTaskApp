using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;

namespace MyTaskApp.Application.Tests.Planning;

public class UserClockTests
{
    /// <summary>2026-09-17 23:30 UTC — já é dia 18 em Tóquio, ainda é 17 em São Paulo.</summary>
    private static readonly DateTimeOffset LateNightUtc = new(2026, 9, 17, 23, 30, 0, TimeSpan.Zero);

    private static UserClock Clock(string? timeZoneId, DateTimeOffset now) =>
        new(
            new FakeTimeProvider(now),
            Options.Create(new ApplicationOptions { TimeZoneId = timeZoneId }),
            NullLogger<UserClock>.Instance);

    [Fact]
    public void Today_UsesTheConfiguredTimeZoneNotUtc()
    {
        Clock("America/Sao_Paulo", LateNightUtc).Today.Should().Be(new DateOnly(2026, 9, 17));
    }

    [Fact]
    public void Today_CrossesTheDateLineForAnEasternTimeZone()
    {
        Clock("Asia/Tokyo", LateNightUtc).Today.Should().Be(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public void CurrentTime_IsTheWallClockOfTheConfiguredZone()
    {
        Clock("America/Sao_Paulo", LateNightUtc).CurrentTime.Should().Be(new TimeOnly(20, 30));
    }

    [Fact]
    public void UnknownTimeZoneId_FallsBackInsteadOfCrashingTheApp()
    {
        // Um id errado no appsettings não pode impedir o app de abrir (§28).
        var clock = Clock("Marte/Olympus_Mons", LateNightUtc);

        clock.TimeZone.Should().Be(TimeZoneInfo.Local);
        clock.Today.Should().Be(DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(LateNightUtc, TimeZoneInfo.Local).DateTime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTimeZoneId_UsesTheMachineTimeZone(string? timeZoneId)
    {
        Clock(timeZoneId, LateNightUtc).TimeZone.Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public void ToLocalDate_ConvertsAnInstantToTheUsersCalendarDay()
    {
        var clock = Clock("Asia/Tokyo", LateNightUtc);

        clock.ToLocalDate(LateNightUtc).Should().Be(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public void ToLocalDate_KeepsTheSameDayWhenTheOffsetDoesNotCrossMidnight()
    {
        var clock = Clock("America/Sao_Paulo", LateNightUtc);
        var noonUtc = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

        clock.ToLocalDate(noonUtc).Should().Be(new DateOnly(2026, 9, 17));
    }

    // As bordas de horário de verão usam America/New_York, e não São Paulo: o
    // Brasil acabou com o horário de verão em 2019, então São Paulo não tem
    // transição futura para exercitar.

    [Fact]
    public void ToInstant_UsesTheConfiguredZoneOffset()
    {
        var clock = Clock("America/Sao_Paulo", LateNightUtc);

        var instant = clock.ToInstant(new DateOnly(2026, 9, 18), new TimeOnly(9, 0));

        instant.Offset.Should().Be(TimeSpan.FromHours(-3));
        instant.Should().Be(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToInstant_NonExistentTime_AdvancesToTheFirstValidInstant()
    {
        // 2026-03-08 02:30 não existe em Nova York: o relógio pula de 02:00 para 03:00.
        var clock = Clock("America/New_York", LateNightUtc);

        var instant = clock.ToInstant(new DateOnly(2026, 3, 8), new TimeOnly(2, 30));

        instant.Offset.Should().Be(TimeSpan.FromHours(-4));
        instant.DateTime.Should().Be(new DateTime(2026, 3, 8, 3, 30, 0));
    }

    [Fact]
    public void ToInstant_AmbiguousTime_UsesTheFirstOccurrence()
    {
        // 2026-11-01 01:30 acontece duas vezes em Nova York; vale a primeira,
        // que é a do horário de verão (-04:00).
        var clock = Clock("America/New_York", LateNightUtc);

        var instant = clock.ToInstant(new DateOnly(2026, 11, 1), new TimeOnly(1, 30));

        instant.Offset.Should().Be(TimeSpan.FromHours(-4));
    }

    [Fact]
    public void ToInstant_Midnight_DoesNotDriftToThePreviousDay()
    {
        var clock = Clock("America/Sao_Paulo", LateNightUtc);

        var instant = clock.ToInstant(new DateOnly(2026, 9, 18), TimeOnly.MinValue);

        clock.ToLocalDate(instant).Should().Be(new DateOnly(2026, 9, 18));
    }

    [Fact]
    public void ToInstant_RoundTripsThroughToLocalDate()
    {
        var clock = Clock("Asia/Tokyo", LateNightUtc);
        var date = new DateOnly(2026, 9, 18);

        clock.ToLocalDate(clock.ToInstant(date, new TimeOnly(9, 0))).Should().Be(date);
    }
}

public class ApplicationOptionsTests
{
    [Fact]
    public void ToNowWindow_UsesTheConfiguredMinutes()
    {
        var options = new ApplicationOptions { NowWindowBeforeMinutes = 5, NowWindowAfterMinutes = 90 };

        var window = options.ToNowWindow();

        window.Before.Should().Be(TimeSpan.FromMinutes(5));
        window.After.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Defaults_MatchTheShippedConfiguration()
    {
        var window = new ApplicationOptions().ToNowWindow();

        window.Should().Be(Domain.Planning.NowWindow.Default);
    }
}
