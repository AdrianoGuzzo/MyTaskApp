using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Planning;

public class TodayClassifierTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);
    private static readonly TimeOnly Now = new(14, 0);
    private static readonly NowWindow Window = NowWindow.Default;

    private static TodayPlacement? Classify(TodayCandidate candidate) =>
        TodayClassifier.Classify(candidate, Today, Now, Window);

    private static TodayCandidate Pending(DateOnly? date, TimeOnly? time = null) =>
        new(date, time, TaskItemStatus.Pending, CompletedOn: null);

    [Fact]
    public void CancelledOccurrence_DoesNotShowUpAtAll()
    {
        var candidate = new TodayCandidate(Today, new TimeOnly(9, 0), TaskItemStatus.Cancelled, null);

        Classify(candidate).Should().BeNull();
    }

    [Fact]
    public void CompletedToday_GoesToTheCompletedSection()
    {
        var candidate = new TodayCandidate(Today, new TimeOnly(9, 0), TaskItemStatus.Completed, Today);

        Classify(candidate)!.Section.Should().Be(TodaySection.Completed);
    }

    [Fact]
    public void CompletedYesterday_LeavesTodaysBoard()
    {
        var candidate = new TodayCandidate(Today, new TimeOnly(9, 0), TaskItemStatus.Completed, Today.AddDays(-1));

        Classify(candidate).Should().BeNull();
    }

    [Fact]
    public void TaskScheduledYesterdayButFinishedToday_CountsAsCompletedToday()
    {
        // Concluí hoje algo que estava marcado para ontem: entra em Concluídas de hoje.
        var candidate = new TodayCandidate(
            Today.AddDays(-1), new TimeOnly(9, 0), TaskItemStatus.Completed, Today);

        Classify(candidate)!.Section.Should().Be(TodaySection.Completed);
    }

    [Fact]
    public void PendingFromAPreviousDay_IsOverdue()
    {
        Classify(Pending(Today.AddDays(-1)))!.Section.Should().Be(TodaySection.Overdue);
    }

    [Fact]
    public void PendingFromLongAgo_IsStillJustOverdue()
    {
        Classify(Pending(Today.AddDays(-45), new TimeOnly(8, 0)))!
            .Section.Should().Be(TodaySection.Overdue);
    }

    [Fact]
    public void PendingTodayWithoutTime_GoesToTheUnscheduledSection()
    {
        Classify(Pending(Today))!.Section.Should().Be(TodaySection.Unscheduled);
    }

    [Fact]
    public void PendingTomorrow_IsNotOnTodaysBoard()
    {
        Classify(Pending(Today.AddDays(1), new TimeOnly(9, 0))).Should().BeNull();
    }

    [Fact]
    public void PendingWithoutAnyDate_BelongsToTheInboxNotToday()
    {
        Classify(Pending(date: null)).Should().BeNull();
    }

    [Theory]
    [InlineData(13, 45)] // início exato da janela (15 min antes)
    [InlineData(14, 0)]  // agora
    [InlineData(15, 0)]  // fim exato da janela (60 min depois)
    public void PendingTodayInsideTheWindow_IsHappeningNow(int hour, int minute)
    {
        Classify(Pending(Today, new TimeOnly(hour, minute)))!
            .Section.Should().Be(TodaySection.Now);
    }

    [Theory]
    [InlineData(13, 44)] // um minuto antes da janela
    [InlineData(15, 1)]  // um minuto depois da janela
    public void PendingTodayOutsideTheWindow_StaysInTheDayList(int hour, int minute)
    {
        Classify(Pending(Today, new TimeOnly(hour, minute)))!
            .Section.Should().Be(TodaySection.Today);
    }

    [Fact]
    public void PendingTodayWhoseTimeAlreadyPassed_IsFlaggedLateWithoutLeavingTheDay()
    {
        // Segue o exemplo do §9: Daily 09:00 continua em HOJE às 14:00.
        var placement = Classify(Pending(Today, new TimeOnly(9, 0)))!;

        placement.Section.Should().Be(TodaySection.Today);
        placement.IsLate.Should().BeTrue();
    }

    [Fact]
    public void PendingTodayStillAhead_IsNotFlaggedLate()
    {
        Classify(Pending(Today, new TimeOnly(18, 0)))!.IsLate.Should().BeFalse();
    }

    [Fact]
    public void OccurrenceInsideTheWindow_IsNeverFlaggedLate()
    {
        Classify(Pending(Today, new TimeOnly(13, 50)))!.IsLate.Should().BeFalse();
    }

    [Fact]
    public void UnscheduledOccurrence_IsNeverFlaggedLate()
    {
        Classify(Pending(Today))!.IsLate.Should().BeFalse();
    }
}

public class TodayClassifierMidnightTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);
    private static readonly NowWindow Window = NowWindow.Default;

    [Fact]
    public void WindowDoesNotWrapPastMidnightIntoTheEarlyMorning()
    {
        // 23:50 + 60 min daria 00:50; sem limite, uma tarefa de 00:30 de hoje
        // (que já passou) apareceria em AGORA.
        var candidate = new TodayCandidate(Today, new TimeOnly(0, 30), TaskItemStatus.Pending, null);

        var placement = TodayClassifier.Classify(candidate, Today, new TimeOnly(23, 50), Window);

        placement!.Section.Should().Be(TodaySection.Today);
        placement.IsLate.Should().BeTrue();
    }

    [Fact]
    public void WindowDoesNotWrapBackwardsIntoThePreviousNight()
    {
        // 00:05 − 15 min daria 23:50; uma tarefa de 23:55 ainda está no futuro.
        var candidate = new TodayCandidate(Today, new TimeOnly(23, 55), TaskItemStatus.Pending, null);

        var placement = TodayClassifier.Classify(candidate, Today, new TimeOnly(0, 5), Window);

        placement!.Section.Should().Be(TodaySection.Today);
        placement.IsLate.Should().BeFalse();
    }

    [Fact]
    public void TaskAtMidnightIsStillReachableByTheWindow()
    {
        var candidate = new TodayCandidate(Today, new TimeOnly(0, 0), TaskItemStatus.Pending, null);

        var placement = TodayClassifier.Classify(candidate, Today, new TimeOnly(0, 5), Window);

        placement!.Section.Should().Be(TodaySection.Now);
    }
}

public class NowWindowTests
{
    [Fact]
    public void Default_LooksSlightlyBackAndFurtherAhead()
    {
        NowWindow.Default.Before.Should().Be(TimeSpan.FromMinutes(15));
        NowWindow.Default.After.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(15, -1)]
    public void NegativeBoundaries_AreRejected(int before, int after)
    {
        var build = () => new NowWindow(
            TimeSpan.FromMinutes(before), TimeSpan.FromMinutes(after));

        build.Should().Throw<DomainException>();
    }

    [Fact]
    public void AWindowLongerThanADay_IsRejected()
    {
        var build = () => new NowWindow(TimeSpan.FromHours(30), TimeSpan.FromMinutes(60));

        build.Should().Throw<DomainException>();
    }
}
