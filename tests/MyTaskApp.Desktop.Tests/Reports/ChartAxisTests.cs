using MyTaskApp.Desktop.Reports;

namespace MyTaskApp.Desktop.Tests.Reports;

/// <summary>O eixo de horas em números redondos (ADR-097).</summary>
public class ChartAxisTests
{
    [Theory]
    [InlineData(0, 8, 2)] // sem horas: uma jornada
    [InlineData(0.4, 0.5, 0.25)]
    [InlineData(3, 3, 1)]
    [InlineData(6.3, 8, 2)]
    [InlineData(9, 10, 2)]
    [InlineData(11, 12, 4)]
    [InlineData(23, 25, 5)]
    [InlineData(160, 200, 50)]
    [InlineData(1730, 2000, 500)]
    public void TheTop_AndTheStep_AreRound(double maxHours, double top, double step) =>
        ChartAxis.For(maxHours).Should().Be((top, step));

    [Theory]
    [InlineData(0.1)]
    [InlineData(7.9)]
    [InlineData(41)]
    [InlineData(999)]
    [InlineData(123456)]
    public void ThereAreNeverMoreThanFiveLines(double maxHours)
    {
        var (top, step) = ChartAxis.For(maxHours);

        top.Should().BeGreaterThanOrEqualTo(maxHours);
        (top / step).Should().BeLessThanOrEqualTo(ChartAxis.MaxLines);
    }
}
