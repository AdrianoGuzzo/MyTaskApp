using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Planning;

/// <summary>A seção PRAZOS (ADR-050): o prazo decide o atraso, não a data da captura.</summary>
public class TodayClassifierDeadlineTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly TimeOnly Now = new(14, 0);
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    private static TodayPlacement? Classify(
        DateOnly? date,
        TimeOnly? time,
        TaskDeadline? deadline,
        TaskItemStatus status = TaskItemStatus.Pending,
        DateOnly? completedOn = null) =>
        TodayClassifier.Classify(new TodayCandidate(date, time, status, completedOn, deadline), Today, Now, NowWindow.Default);

    [Fact]
    public void CapturedYesterday_WithAFutureDeadline_IsNotOverdue()
    {
        Classify(Today.AddDays(-1), null, Friday)!.Section.Should().Be(TodaySection.Deadlines);
    }

    [Fact]
    public void TodayWithoutTime_AndADeadline_GoesToDeadlinesNotUnscheduled()
    {
        Classify(Today, null, Friday)!.Section.Should().Be(TodaySection.Deadlines);
    }

    [Fact]
    public void ScheduledInTheFuture_WithADeadline_IsAlreadyVisible()
    {
        // Sem prazo, uma tarefa de amanhã não aparece; com prazo, a
        // responsabilidade corre desde já.
        Classify(Today.AddDays(2), new TimeOnly(9, 0), Friday)!.Section.Should().Be(TodaySection.Deadlines);
    }

    [Fact]
    public void WithoutAnyDate_ButWithADeadline_IsVisible()
    {
        Classify(null, null, Friday)!.Section.Should().Be(TodaySection.Deadlines);
    }

    [Fact]
    public void ScheduledForATimeToday_FollowsTheDayPlan()
    {
        Classify(Today, new TimeOnly(14, 10), Friday)!.Section.Should().Be(TodaySection.Now);
        Classify(Today, new TimeOnly(17, 0), Friday)!.Section.Should().Be(TodaySection.Today);
    }

    [Fact]
    public void APassedDeadline_IsOverdueWhateverTheSchedule()
    {
        var thisMorning = new TaskDeadline(Today, new TimeOnly(10, 0));

        Classify(Today, new TimeOnly(14, 10), thisMorning)!.Section.Should().Be(TodaySection.Overdue);
        Classify(Today.AddDays(3), null, thisMorning)!.Section.Should().Be(TodaySection.Overdue);
        Classify(null, null, thisMorning)!.Section.Should().Be(TodaySection.Overdue);
    }

    [Fact]
    public void ADeadlineLaterToday_IsNotOverdueYet()
    {
        Classify(Today, null, new TaskDeadline(Today, new TimeOnly(18, 0)))!
            .Section.Should().Be(TodaySection.Deadlines);
    }

    [Fact]
    public void CompletedToday_StillGoesToCompleted()
    {
        Classify(Today.AddDays(-2), null, Friday, TaskItemStatus.Completed, Today)!
            .Section.Should().Be(TodaySection.Completed);
    }

    [Fact]
    public void Cancelled_WithADeadline_StaysHidden()
    {
        Classify(Today, null, Friday, TaskItemStatus.Cancelled).Should().BeNull();
    }
}
