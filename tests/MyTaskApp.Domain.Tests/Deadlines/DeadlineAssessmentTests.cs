using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class DeadlineAssessmentTests
{
    private static readonly TimeSpan SaoPaulo = TimeSpan.FromHours(-3);

    // Segunda-feira, 05/10/2026, 09:00.
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, SaoPaulo);

    private static DeadlineSnapshot Assess(DateOnly date, TimeOnly time, DateTimeOffset? completedAt = null)
    {
        var deadline = new TaskDeadline(date, time);
        var at = new DateTimeOffset(date.ToDateTime(time), SaoPaulo);

        return DeadlineAssessment.Assess(deadline, at, Now, Today, completedAt);
    }

    [Fact]
    public void FarAway_IsOnTrack()
    {
        var snapshot = Assess(Today.AddDays(5), new TimeOnly(18, 0));

        snapshot.Status.Should().Be(DeadlineStatus.OnTrack);
        snapshot.Severity.Should().Be(DeadlineSeverity.Normal);
        snapshot.Remaining.Should().Be(TimeSpan.FromDays(5) + TimeSpan.FromHours(9));
    }

    [Fact]
    public void WithinFortyEightHours_AsksForAttention()
    {
        var snapshot = Assess(Today.AddDays(1), new TimeOnly(18, 0));

        snapshot.Status.Should().Be(DeadlineStatus.DueSoon);
        snapshot.Severity.Should().Be(DeadlineSeverity.Attention);
    }

    [Fact]
    public void JustOverFortyEightHours_IsStillNormal()
    {
        Assess(Today.AddDays(2), new TimeOnly(9, 1)).Severity.Should().Be(DeadlineSeverity.Normal);
        Assess(Today.AddDays(2), new TimeOnly(9, 0)).Severity.Should().Be(DeadlineSeverity.Attention);
    }

    [Fact]
    public void Today_IsUrgentEvenWithHoursToGo()
    {
        var snapshot = Assess(Today, new TimeOnly(23, 0));

        snapshot.Status.Should().Be(DeadlineStatus.DueToday);
        snapshot.Severity.Should().Be(DeadlineSeverity.Urgent);
    }

    [Fact]
    public void TomorrowButWithinEightHours_IsUrgent()
    {
        // Amanhã às 02:00, visto às 20:00: a virada do dia não esconde a pressa.
        var lateNight = new DateTimeOffset(2026, 10, 5, 20, 0, 0, SaoPaulo);
        var deadline = new TaskDeadline(Today.AddDays(1), new TimeOnly(2, 0));
        var at = new DateTimeOffset(2026, 10, 6, 2, 0, 0, SaoPaulo);

        var snapshot = DeadlineAssessment.Assess(deadline, at, lateNight, Today);

        snapshot.Status.Should().Be(DeadlineStatus.DueSoon);
        snapshot.Severity.Should().Be(DeadlineSeverity.Urgent);
    }

    [Fact]
    public void Passed_IsOverdueWithNegativeRemaining()
    {
        var snapshot = Assess(Today, new TimeOnly(6, 0));

        snapshot.Status.Should().Be(DeadlineStatus.Overdue);
        snapshot.Severity.Should().Be(DeadlineSeverity.Overdue);
        snapshot.Remaining.Should().Be(TimeSpan.FromHours(-3));
    }

    [Fact]
    public void AtTheExactMinute_IsAlreadyOverdue()
    {
        Assess(Today, new TimeOnly(9, 0)).Status.Should().Be(DeadlineStatus.Overdue);
    }

    [Fact]
    public void CompletedBeforeTheDeadline_WasMet()
    {
        var snapshot = Assess(Today, new TimeOnly(18, 0), completedAt: Now);

        snapshot.Status.Should().Be(DeadlineStatus.Met);
        snapshot.Severity.Should().Be(DeadlineSeverity.Normal);
        snapshot.Remaining.Should().Be(TimeSpan.FromHours(9));
    }

    [Fact]
    public void CompletedAfterTheDeadline_WasMissed()
    {
        var snapshot = Assess(Today.AddDays(-1), new TimeOnly(18, 0), completedAt: Now);

        snapshot.Status.Should().Be(DeadlineStatus.Missed);
        snapshot.Severity.Should().Be(DeadlineSeverity.Normal);
    }

    [Fact]
    public void HasPassed_ComparesTheWallClock()
    {
        var deadline = new TaskDeadline(Today, new TimeOnly(18, 0));

        deadline.HasPassed(Today, new TimeOnly(17, 59)).Should().BeFalse();
        deadline.HasPassed(Today, new TimeOnly(18, 0)).Should().BeTrue();
        deadline.HasPassed(Today.AddDays(1), new TimeOnly(0, 0)).Should().BeTrue();
        deadline.HasPassed(Today.AddDays(-1), new TimeOnly(23, 59)).Should().BeFalse();
    }
}
