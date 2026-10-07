using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Domain.Tests.Planning;

/// <summary>O histórico dos últimos dias como projeção das conclusões e dos períodos (ADR-053).</summary>
public class ActivityHistoryBuilderTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-3);

    /// <summary>Terça, 06/10/2026.</summary>
    private static readonly DateOnly Today = new(2026, 10, 6);

    /// <summary>Terça, 18:00 no fuso do usuário.</summary>
    private static readonly DateTimeOffset Now = At(6, 18, 0);

    private static readonly Guid Task = Guid.CreateVersion7();

    private static readonly Guid Occurrence = Guid.CreateVersion7();

    private static DateTimeOffset At(int day, int hour, int minute, int month = 10) =>
        new(2026, month, day, hour, minute, 0, Offset);

    private static ActivityHistory Build(
        IEnumerable<ActivityCompletion>? completions = null,
        IEnumerable<ActivityPeriod>? periods = null,
        DateTimeOffset? now = null) =>
        ActivityHistoryBuilder.Build(
            Today,
            7,
            date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Offset),
            instant => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime),
            now ?? Now,
            completions ?? [],
            periods ?? []);

    private static ActivityCompletion Completed(DateTimeOffset at, string title = "Corrigir API", Guid? occurrence = null) =>
        new(Task, occurrence ?? Occurrence, title, at);

    private static ActivityPeriod Worked(
        DateTimeOffset from,
        DateTimeOffset? until,
        string title = "Corrigir API",
        Guid? task = null,
        Guid? occurrence = null) =>
        new(task ?? Task, occurrence ?? Occurrence, title, from, until);

    private static ActivityDay Day(ActivityHistory history, int day, int month = 10) =>
        history.Days.Single(entry => entry.Date == new DateOnly(2026, month, day));

    [Fact]
    public void TheHistory_IsTodayAndTheSixDaysBefore_NewestFirst()
    {
        var history = Build();

        history.Days.Select(day => day.Date).Should().Equal(
            new DateOnly(2026, 10, 6),
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 4),
            new DateOnly(2026, 10, 3),
            new DateOnly(2026, 10, 2),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 9, 30));
    }

    [Fact]
    public void DaysWithoutActivity_StayInTheList()
    {
        var history = Build(completions: [Completed(At(6, 9, 0))]);

        history.Days.Should().HaveCount(7);
        history.Days.Count(day => day.IsEmpty).Should().Be(6);
        history.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void NothingAtAll_IsAnEmptyHistory()
    {
        var history = Build();

        history.IsEmpty.Should().BeTrue();
        history.Completed.Should().Be(0);
        history.Worked.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ACompletion_LandsOnTheDayItWasCompleted()
    {
        var history = Build(completions: [Completed(At(4, 15, 42))]);

        var item = Day(history, 4).Items.Should().ContainSingle().Subject;
        item.IsCompleted.Should().BeTrue();
        item.CompletedAt.Should().Be(At(4, 15, 42));
        item.Worked.Should().Be(TimeSpan.Zero);
        history.Days.Where(day => day.Date != new DateOnly(2026, 10, 4)).Should().OnlyContain(day => day.IsEmpty);
    }

    [Fact]
    public void ACompletionToday_IsToday()
    {
        var history = Build(completions: [Completed(At(6, 17, 42))]);

        Day(history, 6).Items.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void A2330Completion_IsThatDay_EvenThoughItIsTomorrowInUtc()
    {
        // 23:30 de segunda em São Paulo é 02:30 de terça em UTC.
        var history = Build(completions: [Completed(At(5, 23, 30))]);

        Day(history, 5).Items.Should().ContainSingle();
        Day(history, 6).IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(29, 23, 59, false)] // o oitavo dia fica de fora
    [InlineData(30, 0, 0, true)] // a meia-noite do sétimo dia já conta
    public void TheWindow_StartsAtMidnightOfTheSeventhDay(int day, int hour, int minute, bool included)
    {
        var history = Build(completions: [Completed(At(day, hour, minute, month: 9))]);

        history.IsEmpty.Should().Be(!included);
    }

    [Fact]
    public void AWorkedTask_ThatIsNotCompleted_Appears()
    {
        var history = Build(periods: [Worked(At(6, 10, 0), At(6, 10, 45))]);

        var item = Day(history, 6).Items.Should().ContainSingle().Subject;
        item.IsCompleted.Should().BeFalse();
        item.Worked.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void ALongTask_AppearsOnEveryDayItWasWorked_WithThatDaysTime()
    {
        var history = Build(
            completions: [Completed(At(6, 15, 42), "Integração TGC")],
            periods:
            [
                Worked(At(4, 9, 0), At(4, 10, 20), "Integração TGC"),
                Worked(At(5, 14, 0), At(5, 16, 10), "Integração TGC"),
                Worked(At(6, 14, 57), At(6, 15, 42), "Integração TGC"),
            ]);

        Day(history, 4).Items.Should().ContainSingle()
            .Which.Should().Match<ActivityItem>(item => !item.IsCompleted && item.Worked == new TimeSpan(1, 20, 0));
        Day(history, 5).Items.Should().ContainSingle()
            .Which.Should().Match<ActivityItem>(item => !item.IsCompleted && item.Worked == new TimeSpan(2, 10, 0));
        Day(history, 6).Items.Should().ContainSingle()
            .Which.Should().Match<ActivityItem>(item => item.IsCompleted && item.Worked == TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void SeveralPeriodsOnTheSameDay_AreAddedUp()
    {
        var history = Build(periods:
        [
            Worked(At(6, 8, 30), At(6, 9, 20)),
            Worked(At(6, 13, 10), At(6, 14, 45)),
            Worked(At(6, 16, 0), At(6, 16, 35)),
        ]);

        Day(history, 6).Items.Should().ContainSingle().Which.Worked.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void APeriodAcrossMidnight_CountsOnEachDayWhatHappenedInIt()
    {
        var history = Build(periods: [Worked(At(5, 23, 0), At(6, 1, 30))]);

        Day(history, 5).Items.Should().ContainSingle().Which.Worked.Should().Be(TimeSpan.FromHours(1));
        Day(history, 6).Items.Should().ContainSingle().Which.Worked.Should().Be(TimeSpan.FromMinutes(90));
        history.Worked.Should().Be(TimeSpan.FromMinutes(150));
    }

    [Fact]
    public void TheRunningTimer_CountsUpToNow()
    {
        var history = Build(periods: [Worked(At(6, 14, 0), until: null)], now: At(6, 15, 30));

        Day(history, 6).Items.Should().ContainSingle().Which.Worked.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void AnOldTask_WorkedInsideTheWindow_Appears_OnlyWithTheTimeInside()
    {
        // Começou antes da janela: só o que caiu dentro dela conta.
        var history = Build(periods: [Worked(At(29, 23, 0, month: 9), At(30, 1, 0, month: 9))]);

        Day(history, 30, month: 9).Items.Should().ContainSingle().Which.Worked.Should().Be(TimeSpan.FromHours(1));
        history.Worked.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void ARecurringTask_ShowsTheOccurrenceOfEachDay_NotTheSeries()
    {
        var monday = Guid.CreateVersion7();
        var tuesday = Guid.CreateVersion7();

        var history = Build(completions:
        [
            Completed(At(5, 9, 0), "Daily", monday),
            Completed(At(6, 9, 0), "Daily", tuesday),
        ]);

        Day(history, 5).Items.Should().ContainSingle().Which.OccurrenceId.Should().Be(monday);
        Day(history, 6).Items.Should().ContainSingle().Which.OccurrenceId.Should().Be(tuesday);
        history.Completed.Should().Be(2);
    }

    [Fact]
    public void TwoTasksOnTheSameDay_AreTwoLines_MostRecentFirst()
    {
        var other = Guid.CreateVersion7();

        var history = Build(
            completions: [Completed(At(6, 14, 31), "Corrigir API")],
            periods: [Worked(At(6, 16, 0), At(6, 16, 35), "Melhorar documentação", other, other)]);

        Day(history, 6).Items.Select(item => item.Title).Should().Equal("Melhorar documentação", "Corrigir API");
    }

    [Fact]
    public void TheTotals_AreTheCompletionsAndTheTimeInsideTheWindow()
    {
        var other = Guid.CreateVersion7();

        var history = Build(
            completions: [Completed(At(6, 15, 0)), Completed(At(2, 11, 0), "Outra", other)],
            periods: [Worked(At(6, 14, 0), At(6, 15, 0)), Worked(At(3, 9, 0), At(3, 9, 30), "Outra", other, other)]);

        history.Completed.Should().Be(2);
        history.Worked.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void APeriodThatEndsBeforeItStarts_CountsNothing()
    {
        // O relógio do sistema voltou (ADR-052): não é trabalho.
        var history = Build(periods: [Worked(At(6, 14, 0), At(6, 13, 0))]);

        history.IsEmpty.Should().BeTrue();
    }
}
