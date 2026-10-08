using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Dia e hora digitados à mão no diálogo de período.</summary>
public class WallClockInputTests
{
    // Terça, 06/10/2026.
    private static readonly DateOnly Today = new(2026, 10, 6);

    [Theory]
    [InlineData("0831", 8, 31)]
    [InlineData("831", 8, 31)]
    [InlineData("08:31", 8, 31)]
    [InlineData("8:31", 8, 31)]
    [InlineData("8h31", 8, 31)]
    [InlineData("8H", 8, 0)]
    [InlineData("08.31", 8, 31)]
    [InlineData("8", 8, 0)]
    [InlineData("14", 14, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("2359", 23, 59)]
    [InlineData("  1430 ", 14, 30)]
    public void ATime_IsRead(string text, int hour, int minute)
    {
        WallClockInput.TryParseTime(text, out var time).Should().BeTrue();
        time.Should().Be(new TimeOnly(hour, minute));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("24")]
    [InlineData("2400")]
    [InlineData("0860")]
    [InlineData("083")]
    [InlineData("8:5")]
    [InlineData("08311")]
    [InlineData("oito")]
    [InlineData("-1")]
    public void ATime_IsRefused(string? text)
    {
        WallClockInput.TryParseTime(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("06/10/2026", 2026, 10, 6)]
    [InlineData("6/10/2026", 2026, 10, 6)]
    [InlineData("06/10/26", 2026, 10, 6)]
    [InlineData("6-10-2026", 2026, 10, 6)]
    [InlineData("6.10.2026", 2026, 10, 6)]
    [InlineData("06102026", 2026, 10, 6)]
    [InlineData("061026", 2026, 10, 6)]
    [InlineData("0510", 2026, 10, 5)]
    [InlineData("5/10", 2026, 10, 5)]
    [InlineData("3", 2026, 10, 3)]
    [InlineData("hoje", 2026, 10, 6)]
    [InlineData("Ontem", 2026, 10, 5)]
    public void ADate_IsRead(string text, int year, int month, int day)
    {
        WallClockInput.TryParseDate(text, Today, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData("2812", 2025, 12, 28)]
    [InlineData("28/12", 2025, 12, 28)]
    [InlineData("10", 2026, 9, 10)]
    [InlineData("31", 2026, 8, 31)]
    public void WithoutTheYear_TheLatestDateUpToToday_IsTaken(string text, int year, int month, int day)
    {
        WallClockInput.TryParseDate(text, Today, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Fact]
    public void The29thOfFebruary_WithoutTheYear_GoesBackToTheLastLeapYear()
    {
        WallClockInput.TryParseDate("2902", Today, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(2024, 2, 29));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("32/10/2026")]
    [InlineData("31/09/2026")]
    [InlineData("29/02/2026")]
    [InlineData("06/13")]
    [InlineData("0")]
    [InlineData("061")]
    [InlineData("0610202")]
    [InlineData("06/10/202")]
    [InlineData("amanhã")]
    public void ADate_IsRefused(string? text)
    {
        WallClockInput.TryParseDate(text, Today, out _).Should().BeFalse();
    }

    [Fact]
    public void Writing_IsTheBrazilianFormat()
    {
        WallClockInput.Format(new TimeOnly(8, 5)).Should().Be("08:05");
        WallClockInput.Format(new DateOnly(2026, 10, 6)).Should().Be("06/10/2026");
    }
}
