using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>O relatório de horas por dia, mês e ano (ADR-097), sem banco.</summary>
public class GetHoursHistoryHandlerTests
{
    /// <summary>Terça, 27/10/2026, 15:00 em São Paulo.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private static readonly Guid Report = Guid.NewGuid();

    private static readonly Guid Review = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GetHoursHistoryHandler Handler, FakeTimeEntryReportQuery Query, IUserClock Clock) Build(DateTimeOffset? now = null)
    {
        var time = new FakeTimeProvider(now ?? Now);
        var clock = new UserClock(time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        var query = new FakeTimeEntryReportQuery();

        return (new GetHoursHistoryHandler(query, clock, time), query, clock);
    }

    private static void Add(
        FakeTimeEntryReportQuery query,
        IUserClock clock,
        DateOnly day,
        int fromHour,
        int toHour,
        Guid? task = null,
        int fromMinute = 0,
        int toMinute = 0) =>
        query.Rows.Add(Row(
            clock.ToInstant(day, new TimeOnly(fromHour, fromMinute)),
            clock.ToInstant(day, new TimeOnly(toHour, toMinute)),
            task));

    private static TimeEntryReportRow Row(DateTimeOffset start, DateTimeOffset? end, Guid? task = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), task ?? Report, task == Review ? "Revisão" : "Relatório", null, [], start, end,
            TimeEntrySource.Manual, null, start, null, false, false);

    private static DateOnly Oct(int day) => new(2026, 10, day);

    // --- Escala de dias ---------------------------------------------------------

    [Fact]
    public async Task TheDayScale_HasEveryDayOfTheMonth_EvenTheEmptyOnes()
    {
        var (handler, _, _) = Build();

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Title.Should().Be("outubro de 2026");
        history.From.Should().Be(Oct(1));
        history.To.Should().Be(Oct(31));
        history.Buckets.Should().HaveCount(31);
        history.Days.Should().BeSameAs(history.Buckets);
        history.IsEmpty.Should().BeTrue();
        history.Best.Should().BeNull();

        var saturday = history.Buckets[23];
        saturday.Key.Should().Be("2026-10-24");
        saturday.Label.Should().Be("24");
        saturday.LongLabel.Should().Be("sáb, 24/10/2026");
        saturday.Summary.Should().Be("sáb, 24/10 · sem horas");
        saturday.IsWeekend.Should().BeTrue();

        history.Buckets[26].IsCurrent.Should().BeTrue();
        history.Buckets[26].IsFuture.Should().BeFalse();
        history.Buckets[27].IsFuture.Should().BeTrue();
        history.Buckets[27].Summary.Should().Be("qua, 28/10");
        history.ElapsedDays.Should().Be(27);
    }

    [Fact]
    public async Task TheDays_AddUpThePeriods_SplittingTheOneAcrossMidnight()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, Oct(14), 9, 12, toMinute: 30);
        query.Rows.Add(Row(clock.ToInstant(Oct(15), new TimeOnly(23, 0)), clock.ToInstant(Oct(16), new TimeOnly(1, 0))));

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Buckets[13].Total.Should().Be(TimeSpan.FromMinutes(210));
        history.Buckets[13].Summary.Should().Be("qua, 14/10 · 3h 30min · 1 lançamento");
        history.Buckets[13].Level.Should().Be(2);
        history.Buckets[14].Total.Should().Be(TimeSpan.FromHours(1));
        history.Buckets[15].Total.Should().Be(TimeSpan.FromHours(1));
        history.Buckets[15].EntryCount.Should().Be(1);

        history.Total.Should().Be(TimeSpan.FromMinutes(330));
        history.EntryCount.Should().Be(2);
        history.ActiveDays.Should().Be(3);
        history.AveragePerActiveDay.Should().Be(TimeSpan.FromMinutes(110));
        history.AveragePerActiveBucket.Should().Be(TimeSpan.FromMinutes(110));
        history.Best!.Key.Should().Be("2026-10-14");
        history.IncludesRunning.Should().BeFalse();
    }

    [Fact]
    public async Task TheRunningTimer_CountsUntilNow()
    {
        var (handler, query, _) = Build();
        query.Rows.Add(Row(Now.AddMinutes(-45), null));

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Total.Should().Be(TimeSpan.FromMinutes(45));
        history.Buckets[26].Total.Should().Be(TimeSpan.FromMinutes(45));
        history.IncludesRunning.Should().BeTrue();
    }

    [Fact]
    public async Task AnAnchorInTheFuture_BecomesToday()
    {
        var (handler, _, _) = Build();

        var history = await handler.HandleAsync(new GetHoursHistory(Anchor: new DateOnly(2027, 5, 1)), Ct);

        history.Title.Should().Be("outubro de 2026");
    }

    [Fact]
    public async Task TheNavigation_GoesBack_ButNotIntoTheFuture()
    {
        var (handler, _, _) = Build();

        var current = await handler.HandleAsync(new GetHoursHistory(), Ct);
        var past = await handler.HandleAsync(new GetHoursHistory(Anchor: new DateOnly(2026, 8, 20)), Ct);

        current.PreviousAnchor.Should().Be(new DateOnly(2026, 9, 1));
        current.NextAnchor.Should().BeNull();
        past.Title.Should().Be("agosto de 2026");
        past.NextAnchor.Should().Be(new DateOnly(2026, 9, 1));
    }

    // --- Escala de meses --------------------------------------------------------

    [Fact]
    public async Task TheMonthScale_HasTwelveMonths_AndTheDaysOfTheYear()
    {
        var (handler, query, clock) = Build();
        query.Rows.Add(Row(clock.ToInstant(new DateOnly(2026, 9, 30), new TimeOnly(23, 0)), clock.ToInstant(Oct(1), new TimeOnly(1, 0))));
        Add(query, clock, Oct(2), 8, 17);

        var history = await handler.HandleAsync(new GetHoursHistory(HoursScale.Month), Ct);

        history.Title.Should().Be("2026");
        history.Buckets.Select(bucket => bucket.Label).Should().Equal(
            "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez");
        history.Days.Should().HaveCount(365);

        var september = history.Buckets[8];
        var october = history.Buckets[9];
        september.Total.Should().Be(TimeSpan.FromHours(1));
        september.EntryCount.Should().Be(1);
        october.Total.Should().Be(TimeSpan.FromHours(10));
        october.EntryCount.Should().Be(2);
        october.ActiveDays.Should().Be(2);
        october.LongLabel.Should().Be("outubro de 2026");
        october.Summary.Should().Be("outubro de 2026 · 10h · 2 dias · 2 lançamentos");
        october.IsCurrent.Should().BeTrue();
        history.Buckets[10].IsFuture.Should().BeTrue();

        history.EntryCount.Should().Be(2);
        history.ActiveDays.Should().Be(3);
        history.AveragePerActiveBucket.Should().Be(TimeSpan.FromMinutes(330));
        history.Days.Single(day => day.From == Oct(2)).Level.Should().Be(4);
        history.Best!.Key.Should().Be("2026-10");
    }

    [Fact]
    public async Task TheMonthScale_NavigatesByYear()
    {
        var (handler, _, _) = Build();

        var history = await handler.HandleAsync(new GetHoursHistory(HoursScale.Month, new DateOnly(2025, 6, 10)), Ct);

        history.Title.Should().Be("2025");
        history.PreviousAnchor.Should().Be(new DateOnly(2024, 1, 1));
        history.NextAnchor.Should().Be(new DateOnly(2026, 1, 1));
    }

    // --- Escala de anos ---------------------------------------------------------

    [Fact]
    public async Task TheYearScale_GoesFromTheFirstYearWithHours_ToThisOne()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, new DateOnly(2024, 3, 5), 9, 11);
        Add(query, clock, Oct(5), 9, 10);

        var history = await handler.HandleAsync(new GetHoursHistory(HoursScale.Year), Ct);

        history.Title.Should().Be("Todos os anos");
        history.Buckets.Select(bucket => (bucket.Label, bucket.Total)).Should().Equal(
            ("2024", TimeSpan.FromHours(2)),
            ("2025", TimeSpan.Zero),
            ("2026", TimeSpan.FromHours(1)));
        history.Buckets[2].IsCurrent.Should().BeTrue();
        history.Days.Should().BeEmpty();
        history.Comparison.Should().BeNull();
        history.PreviousAnchor.Should().BeNull();
        history.NextAnchor.Should().BeNull();
        query.LastCriteria!.Since.Should().BeNull();
    }

    [Fact]
    public async Task TheYearScale_WithoutHours_IsThisYear()
    {
        var (handler, _, _) = Build();

        var history = await handler.HandleAsync(new GetHoursHistory(HoursScale.Year), Ct);

        history.Buckets.Should().ContainSingle().Which.Label.Should().Be("2026");
    }

    // --- Comparação -------------------------------------------------------------

    [Fact]
    public async Task TheCurrentMonth_IsComparedWithTheSameStretchOfTheLastOne()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, new DateOnly(2026, 9, 10), 9, 11);
        Add(query, clock, new DateOnly(2026, 9, 28), 9, 12); // depois do dia 27: fica de fora
        Add(query, clock, Oct(5), 9, 12);

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Comparison.Should().Be(new HoursComparison("vs 1–27 de setembro", TimeSpan.FromHours(2), 0.5));
        history.Total.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task AFinishedMonth_IsComparedWhole()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, new DateOnly(2026, 8, 31), 9, 13);
        Add(query, clock, new DateOnly(2026, 9, 30), 9, 11);

        var history = await handler.HandleAsync(new GetHoursHistory(Anchor: new DateOnly(2026, 9, 15)), Ct);

        history.Comparison.Should().Be(new HoursComparison("vs agosto", TimeSpan.FromHours(4), -0.5));
    }

    [Fact]
    public async Task WithoutHoursBefore_ThereIsNoChange()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, Oct(5), 9, 12);

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Comparison!.Previous.Should().Be(TimeSpan.Zero);
        history.Comparison.Change.Should().BeNull();
    }

    [Fact]
    public async Task TheStretch_StopsAtTheEndOfAShorterMonth()
    {
        // 31/03, 15:00: março até o dia 31 contra fevereiro até o dia 28.
        var (handler, _, _) = Build(new DateTimeOffset(2026, 3, 31, 18, 0, 0, TimeSpan.Zero));

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Comparison!.Label.Should().Be("vs 1–28 de fevereiro");
    }

    [Fact]
    public async Task January_IsComparedWithDecemberOfTheYearBefore()
    {
        var (handler, _, _) = Build(new DateTimeOffset(2026, 1, 10, 18, 0, 0, TimeSpan.Zero));

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Comparison!.Label.Should().Be("vs 1–10 de dezembro de 2025");
    }

    [Fact]
    public async Task TheYear_IsComparedWithTheLastOne()
    {
        var (handler, query, clock) = Build();
        Add(query, clock, new DateOnly(2025, 10, 20), 9, 17);
        Add(query, clock, new DateOnly(2025, 11, 20), 9, 17); // depois de 27/10: fica de fora
        Add(query, clock, Oct(5), 9, 13);

        var current = await handler.HandleAsync(new GetHoursHistory(HoursScale.Month), Ct);
        var past = await handler.HandleAsync(new GetHoursHistory(HoursScale.Month, new DateOnly(2025, 1, 1)), Ct);

        current.Comparison.Should().Be(new HoursComparison("vs 2025 até 27/10", TimeSpan.FromHours(8), -0.5));
        past.Comparison!.Label.Should().Be("vs 2024");
    }

    // --- Filtros e tarefas --------------------------------------------------------

    [Fact]
    public async Task TheTag_ReachesTheQuery()
    {
        var (handler, query, clock) = Build();
        var tag = Guid.NewGuid();

        await handler.HandleAsync(new GetHoursHistory(TagId: tag), Ct);

        query.LastCriteria!.TagId.Should().Be(tag);
        query.LastCriteria.IncludeTrashed.Should().BeFalse();
        query.LastCriteria.Since.Should().Be(clock.ToInstant(new DateOnly(2026, 9, 1), TimeOnly.MinValue));
        query.LastCriteria.Until.Should().Be(clock.ToInstant(new DateOnly(2026, 11, 1), TimeOnly.MinValue));
    }

    [Fact]
    public async Task TheTasks_AreCutToThePeriod_FromMostToLeast()
    {
        var (handler, query, clock) = Build();
        query.Rows.Add(Row(clock.ToInstant(new DateOnly(2026, 9, 30), new TimeOnly(22, 0)), clock.ToInstant(Oct(1), new TimeOnly(2, 0)), Review));
        Add(query, clock, Oct(3), 9, 12);
        Add(query, clock, Oct(4), 9, 10);

        var history = await handler.HandleAsync(new GetHoursHistory(), Ct);

        history.Tasks.Select(task => (task.Label, task.Total, task.EntryCount)).Should().Equal(
            ("Relatório", TimeSpan.FromHours(4), 2),
            ("Revisão", TimeSpan.FromHours(2), 1));
        history.Tasks[0].Key.Should().Be(Report.ToString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(119, 1)]
    [InlineData(120, 2)]
    [InlineData(299, 2)]
    [InlineData(300, 3)]
    [InlineData(479, 3)]
    [InlineData(480, 4)]
    [InlineData(900, 4)]
    public void TheLevels_HaveFixedBands(int minutes, int level) =>
        HoursLevels.Of(TimeSpan.FromMinutes(minutes)).Should().Be(level);
}
