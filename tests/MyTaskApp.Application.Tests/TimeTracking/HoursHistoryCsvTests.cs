using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>O relatório de horas como planilha do Excel em pt-BR (ADR-097).</summary>
public class HoursHistoryCsvTests
{
    private static HoursBucket Bucket(string label, DateOnly from, DateOnly to, TimeSpan total, int count, bool future = false) =>
        new(label, from, to, label, label, label, total, count, total > TimeSpan.Zero ? 1 : 0, false, false, future, 0);

    private static HoursHistory History(HoursScale scale, params HoursBucket[] buckets) =>
        new(scale, buckets[0].From, buckets[^1].To, "título", null, null, buckets, [],
            buckets.Aggregate(TimeSpan.Zero, (sum, bucket) => sum + bucket.Total), 0, 0, 0,
            TimeSpan.Zero, TimeSpan.Zero, null, null, [], false);

    [Fact]
    public void TheSheet_UsesSemicolonsDecimalCommasAndCrlf_AndKeepsTheEmptyBlocks()
    {
        var history = History(
            HoursScale.Month,
            Bucket("setembro de 2026", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), TimeSpan.Zero, 0),
            Bucket("outubro de 2026", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TimeSpan.FromMinutes(380), 4),
            Bucket("novembro de 2026", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), TimeSpan.Zero, 0, future: true));

        HoursHistoryCsv.Build(history).Should().Be(
            "Período;Início;Fim;Horas;Duração;Lançamentos;% do total\r\n" +
            "setembro de 2026;2026-09-01;2026-09-30;0,00;0min;0;0,0\r\n" +
            "outubro de 2026;2026-10-01;2026-10-31;6,33;6h 20min;4;100,0\r\n");
    }

    [Fact]
    public void ALabelWithASeparator_GoesInQuotes()
    {
        var history = History(
            HoursScale.Day,
            Bucket("a;\"b\"", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), TimeSpan.FromHours(1), 1));

        HoursHistoryCsv.Build(history).Should().Contain("\r\n\"a;\"\"b\"\"\";2026-10-01;");
    }

    [Theory]
    [InlineData(HoursScale.Day, "horas-2026-10-por-dia.csv")]
    [InlineData(HoursScale.Month, "horas-2026-por-mes.csv")]
    [InlineData(HoursScale.Year, "horas-por-ano.csv")]
    public void TheSuggestedName_SaysThePeriodAndTheScale(HoursScale scale, string name)
    {
        var history = History(scale, Bucket("x", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TimeSpan.Zero, 0));

        HoursHistoryCsv.SuggestedFileName(history).Should().Be(name);
    }
}
