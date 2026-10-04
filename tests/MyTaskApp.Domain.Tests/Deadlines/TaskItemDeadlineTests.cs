using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class TaskItemDeadlineTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-3));

    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    private static (TaskItem Task, Guid OccurrenceId) NewTask()
    {
        var task = TaskItem.Create("Implementar módulo de nutrição", Now, schedule: TaskSchedule.On(new DateOnly(2026, 10, 5)));

        return (task, task.Occurrences[0].Id);
    }

    [Fact]
    public void ANewTask_HasNoDeadline()
    {
        var (task, _) = NewTask();

        task.Occurrences[0].Deadline.Should().BeNull();
        task.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.None);
        task.DeadlineAlerts.Should().BeNull();
        task.NextAction.Should().BeNull();
        task.Estimate.Should().BeNull();
    }

    [Fact]
    public void SettingADeadline_KeepsTheScheduleAndTheReminder()
    {
        // §1: horário, lembrete e prazo são independentes.
        var (task, id) = NewTask();
        task.Occurrences[0].ArmReminder(Now.AddHours(1));

        var occurrence = task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);

        occurrence.Deadline.Should().Be(Friday);
        occurrence.ScheduledDate.Should().Be(new DateOnly(2026, 10, 5));
        occurrence.Reminder.NextFireAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void SettingADeadline_StartsTheAlertsFromTheGivenStage()
    {
        var (task, id) = NewTask();
        task.MarkDeadlineAlerted(id, DeadlineAlertStage.Overdue, Now);

        var occurrence = task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.OneDay);

        occurrence.DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.OneDay);
        occurrence.DeadlineAlert.LastAlertAtUtc.Should().BeNull();
    }

    [Fact]
    public void Rescheduling_DoesNotMoveTheDeadline()
    {
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);

        task.RescheduleOccurrence(id, TaskSchedule.At(new DateOnly(2026, 10, 6), new TimeOnly(14, 0)));

        task.Occurrences[0].Deadline.Should().Be(Friday);
    }

    [Fact]
    public void ClearingTheDeadline_ForgetsTheAlerts()
    {
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);
        task.MarkDeadlineAlerted(id, DeadlineAlertStage.OneDay, Now);

        var occurrence = task.ClearOccurrenceDeadline(id);

        occurrence.Deadline.Should().BeNull();
        occurrence.DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.None);
    }

    [Fact]
    public void ClearingWithoutADeadline_IsRefused()
    {
        var (task, id) = NewTask();

        var clear = () => task.ClearOccurrenceDeadline(id);

        clear.Should().Throw<DomainException>().WithMessage("*não tem prazo*");
    }

    [Fact]
    public void Completing_PreservesTheOriginalDeadline()
    {
        // §22: o prazo original fica, para dizer depois se a entrega foi no prazo.
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);

        task.CompleteOccurrence(id, Now.AddDays(3));

        task.Occurrences[0].Deadline.Should().Be(Friday);
    }

    [Fact]
    public void TheDeadlineOfACompletedOccurrence_CannotChange()
    {
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);
        task.CompleteOccurrence(id, Now);

        var change = () => task.SetOccurrenceDeadline(id, Friday with { Time = new TimeOnly(20, 0) }, DeadlineAlertStage.None);
        var clear = () => task.ClearOccurrenceDeadline(id);

        change.Should().Throw<DomainException>().WithMessage("*pendente*");
        clear.Should().Throw<DomainException>().WithMessage("*pendente*");
    }

    [Fact]
    public void ReopeningAnOccurrence_KeepsItsDeadline()
    {
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);
        task.CompleteOccurrence(id, Now);

        task.ReopenOccurrence(id);

        task.Occurrences[0].Deadline.Should().Be(Friday);
    }

    [Fact]
    public void AnArchivedTask_CannotGetADeadline()
    {
        var (task, id) = NewTask();
        task.Archive(Now);

        var set = () => task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);

        set.Should().Throw<DomainException>().WithMessage("*arquivado*");
    }

    [Fact]
    public void SnoozingTheAlert_DoesNotMoveTheDeadline()
    {
        // §21: adiar o aviso não é adiar o prazo.
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);

        var occurrence = task.SnoozeDeadlineAlert(id, Now.AddMinutes(30));

        occurrence.Deadline.Should().Be(Friday);
        occurrence.DeadlineAlert.SnoozedUntilUtc.Should().Be(Now.AddMinutes(30));
    }

    [Fact]
    public void SnoozingWithoutADeadline_IsRefused()
    {
        var (task, id) = NewTask();

        var snooze = () => task.SnoozeDeadlineAlert(id, Now);

        snooze.Should().Throw<DomainException>().WithMessage("*não tem prazo*");
    }

    [Fact]
    public void SnoozingACompletedOccurrence_IsRefused()
    {
        var (task, id) = NewTask();
        task.SetOccurrenceDeadline(id, Friday, DeadlineAlertStage.None);
        task.CompleteOccurrence(id, Now);

        var snooze = () => task.SnoozeDeadlineAlert(id, Now.AddHours(1));

        snooze.Should().Throw<DomainException>().WithMessage("*pendente*");
    }

    [Fact]
    public void TheAlertOverride_CanBeSetAndCleared()
    {
        var (task, _) = NewTask();

        task.ChangeDeadlineAlerts(DeadlineAlertStage.None);
        task.DeadlineAlerts.Should().Be(DeadlineAlertStage.None);

        task.ChangeDeadlineAlerts(DeadlineAlertStages.OnlyTheDayBefore);
        task.DeadlineAlerts.Should().Be(DeadlineAlertStages.OnlyTheDayBefore);

        task.ChangeDeadlineAlerts(null);
        task.DeadlineAlerts.Should().BeNull();
    }

    [Fact]
    public void AnUnknownAlertOverride_IsRefused()
    {
        var (task, _) = NewTask();

        var change = () => task.ChangeDeadlineAlerts((DeadlineAlertStage)256);

        change.Should().Throw<DomainException>();
    }

    [Fact]
    public void ThePlan_IsNormalized()
    {
        var (task, _) = NewTask();

        task.ChangePlan("  Criar endpoint POST /diets  ", TimeSpan.FromHours(6));

        task.NextAction.Should().Be("Criar endpoint POST /diets");
        task.Estimate.Should().Be(TimeSpan.FromHours(6));

        task.ChangePlan("   ", null);

        task.NextAction.Should().BeNull();
        task.Estimate.Should().BeNull();
    }

    [Fact]
    public void AnInvalidPlan_ChangesNothing()
    {
        var (task, _) = NewTask();
        task.ChangePlan("Primeiro passo", TimeSpan.FromHours(2));

        var tooLong = () => task.ChangePlan(new string('x', TaskItem.MaxNextActionLength + 1), TimeSpan.FromHours(3));
        var negative = () => task.ChangePlan("Outro passo", TimeSpan.FromHours(-1));
        var huge = () => task.ChangePlan("Outro passo", TimeSpan.FromHours(1000));

        tooLong.Should().Throw<DomainException>();
        negative.Should().Throw<DomainException>();
        huge.Should().Throw<DomainException>();
        task.NextAction.Should().Be("Primeiro passo");
        task.Estimate.Should().Be(TimeSpan.FromHours(2));
    }
}
