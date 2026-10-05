using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class DeadlineAlertingTests
{
    // Sexta-feira, 09/10/2026, 18:00 em São Paulo.
    private static readonly DateTimeOffset Deadline =
        new(2026, 10, 9, 18, 0, 0, TimeSpan.FromHours(-3));

    private static readonly DeadlineAlertStage Defaults = DeadlineAlertPolicy.Default.Stages;

    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    private static DeadlineAlertStage? Decide(DeadlineAlertState state, DateTimeOffset now) =>
        DeadlineAlerting.Decide(Defaults, Daily, Deadline, state, now);

    private static DeadlineAlertState Fresh(DeadlineAlertStage initial = DeadlineAlertStage.None) =>
        new Tracked(initial).State;

    /// <summary>O estado só muda pela raiz, então os testes também passam por ela.</summary>
    private sealed class Tracked
    {
        private readonly TaskItem _task = TaskItem.Create("Entregar módulo", Deadline.AddDays(-10));

        public Tracked(DeadlineAlertStage initial = DeadlineAlertStage.None) =>
            _task.SetOccurrenceDeadline(
                OccurrenceId,
                new TaskDeadline(new DateOnly(2026, 10, 9), new TimeOnly(18, 0)),
                initial);

        public DeadlineAlertState State => _task.Occurrences[0].DeadlineAlert;

        private Guid OccurrenceId => _task.Occurrences[0].Id;

        public void Alerted(DeadlineAlertStage stage, DateTimeOffset at) =>
            _task.MarkDeadlineAlerted(OccurrenceId, stage, at);

        public void Snooze(DateTimeOffset until) => _task.SnoozeDeadlineAlert(OccurrenceId, until);
    }

    [Fact]
    public void BeforeTheFirstEnabledStage_NothingIsSaid()
    {
        // §4: com mais de 24 horas pela frente o padrão não avisa — só mostra.
        Decide(Fresh(), Deadline.AddDays(-5)).Should().BeNull();
        Decide(Fresh(), Deadline.AddHours(-24).AddSeconds(-1)).Should().BeNull();
    }

    [Fact]
    public void AtTheExactThreshold_TheStageFires()
    {
        Decide(Fresh(), Deadline.AddHours(-24)).Should().Be(DeadlineAlertStage.OneDay);
        Decide(Fresh(), Deadline.AddHours(-8)).Should().Be(DeadlineAlertStage.EightHours);
        Decide(Fresh(), Deadline.AddHours(-2)).Should().Be(DeadlineAlertStage.TwoHours);
        Decide(Fresh(), Deadline).Should().Be(DeadlineAlertStage.Overdue);
    }

    [Fact]
    public void AStageAlreadyAnnounced_IsNotRepeated()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        var now = Deadline.AddHours(-20);
        tracked.Alerted(Decide(state, now)!.Value, now);

        Decide(state, now.AddMinutes(1)).Should().BeNull();
        Decide(state, now.AddHours(11)).Should().BeNull();
    }

    [Fact]
    public void TheNextStage_FiresOnceItArrives()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        tracked.Alerted(DeadlineAlertStage.OneDay, Deadline.AddHours(-24));

        Decide(state, Deadline.AddHours(-8)).Should().Be(DeadlineAlertStage.EightHours);
    }

    [Fact]
    public void AnAppClosedThroughSeveralStages_SaysOnlyTheMostSevereOnce()
    {
        // Fechado da véspera até faltar uma hora: um aviso, o de 2 horas.
        var tracked = new Tracked();
        var state = tracked.State;
        var reopenedAt = Deadline.AddHours(-1);

        var stage = Decide(state, reopenedAt);
        stage.Should().Be(DeadlineAlertStage.TwoHours);

        tracked.Alerted(stage!.Value, reopenedAt);
        Decide(state, reopenedAt.AddSeconds(30)).Should().BeNull();
    }

    [Fact]
    public void ADisabledStage_IsSkipped()
    {
        var onlyTheDayBefore = DeadlineAlertStages.OnlyTheDayBefore;

        DeadlineAlerting.Decide(onlyTheDayBefore, null, Deadline, Fresh(DeadlineAlertStage.OneDay), Deadline.AddHours(-1))
            .Should().BeNull("8 h e 2 h estão desligados");
        DeadlineAlerting.Decide(onlyTheDayBefore, null, Deadline, Fresh(DeadlineAlertStage.OneDay), Deadline.AddMinutes(1))
            .Should().Be(DeadlineAlertStage.Overdue);
    }

    [Fact]
    public void NoEnabledStage_NeverFires()
    {
        DeadlineAlerting.Decide(DeadlineAlertStage.None, Daily, Deadline, Fresh(), Deadline.AddDays(3))
            .Should().BeNull();
    }

    [Fact]
    public void TheInitialStage_IsNotAnnounced()
    {
        // Prazo definido faltando 5 horas: o degrau de 8 h já nasce cruzado.
        var state = Fresh(DeadlineAlertStage.EightHours);

        Decide(state, Deadline.AddHours(-5)).Should().BeNull();
        Decide(state, Deadline.AddHours(-2)).Should().Be(DeadlineAlertStage.TwoHours);
    }

    [Fact]
    public void Overdue_RepeatsOnlyAfterTheInterval()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        tracked.Alerted(DeadlineAlertStage.Overdue, Deadline);

        Decide(state, Deadline.AddHours(23)).Should().BeNull();
        Decide(state, Deadline.AddDays(1)).Should().Be(DeadlineAlertStage.Overdue);
    }

    [Fact]
    public void Overdue_ClosedForThreeDays_RepeatsOnceAndCountsFromThere()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        tracked.Alerted(DeadlineAlertStage.Overdue, Deadline);
        var reopenedAt = Deadline.AddDays(3).AddHours(5);

        Decide(state, reopenedAt).Should().Be(DeadlineAlertStage.Overdue);
        tracked.Alerted(DeadlineAlertStage.Overdue, reopenedAt);

        Decide(state, reopenedAt.AddHours(23)).Should().BeNull();
        Decide(state, reopenedAt.AddDays(1)).Should().Be(DeadlineAlertStage.Overdue);
    }

    [Fact]
    public void Overdue_WithoutRepeat_SaysItOnce()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        tracked.Alerted(DeadlineAlertStage.Overdue, Deadline);

        DeadlineAlerting.Decide(Defaults, null, Deadline, state, Deadline.AddDays(10)).Should().BeNull();
    }

    [Fact]
    public void Snoozed_HoldsEverythingUntilItEnds_ThenRepeatsTheCurrentStage()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        var firedAt = Deadline.AddHours(-8);
        tracked.Alerted(DeadlineAlertStage.EightHours, firedAt);
        tracked.Snooze(firedAt.AddHours(1));

        Decide(state, firedAt.AddMinutes(59)).Should().BeNull();
        Decide(state, firedAt.AddHours(1)).Should().Be(DeadlineAlertStage.EightHours);
    }

    [Fact]
    public void Snoozed_PastANewStage_SaysTheNewStageWhenItEnds()
    {
        var tracked = new Tracked();
        var state = tracked.State;
        tracked.Alerted(DeadlineAlertStage.EightHours, Deadline.AddHours(-3));
        tracked.Snooze(Deadline.AddMinutes(-90));

        Decide(state, Deadline.AddHours(-2)).Should().BeNull("o adiamento segura até o fim");
        Decide(state, Deadline.AddMinutes(-90)).Should().Be(DeadlineAlertStage.TwoHours);
    }

    [Fact]
    public void MarkingAlerted_EndsTheSnooze()
    {
        var tracked = new Tracked();
        tracked.Snooze(Deadline);

        tracked.Alerted(DeadlineAlertStage.TwoHours, Deadline.AddHours(-1));

        tracked.State.SnoozedUntilUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(-200, DeadlineAlertStage.None)]
    [InlineData(-24, DeadlineAlertStage.OneDay)]
    [InlineData(-7, DeadlineAlertStage.EightHours)]
    [InlineData(-1, DeadlineAlertStage.TwoHours)]
    [InlineData(5, DeadlineAlertStage.Overdue)]
    public void CurrentStage_IsTheMostSevereEnabledOneCrossed(int hoursFromDeadline, DeadlineAlertStage expected)
    {
        DeadlineAlerting.CurrentStage(Defaults, Deadline, Deadline.AddHours(hoursFromDeadline))
            .Should().Be(expected);
    }

    [Fact]
    public void CurrentStage_WithEveryStageOn_StartsAWeekBefore()
    {
        DeadlineAlerting.CurrentStage(DeadlineAlertStage.All, Deadline, Deadline.AddDays(-7))
            .Should().Be(DeadlineAlertStage.SevenDays);
        DeadlineAlerting.CurrentStage(DeadlineAlertStage.All, Deadline, Deadline.AddDays(-3))
            .Should().Be(DeadlineAlertStage.ThreeDays);
    }

    [Fact]
    public void LeadTimeOfAnUnknownStage_IsAProgrammingError()
    {
        var lead = () => DeadlineAlertStages.LeadTimeOf(DeadlineAlertStage.All);

        lead.Should().Throw<ArgumentOutOfRangeException>();
    }
}
