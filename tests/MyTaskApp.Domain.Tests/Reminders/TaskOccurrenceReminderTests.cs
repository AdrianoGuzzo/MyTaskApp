using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Reminders;

public class TaskOccurrenceReminderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 17, 14, 0, 0, TimeSpan.FromHours(-3));

    private static readonly DateTimeOffset FireAt = Now.AddHours(1);

    [Fact]
    public void ANewOccurrence_HasNoReminderUntilItIsArmed()
    {
        var occurrence = PendingOccurrence();

        occurrence.Reminder.IsArmed.Should().BeFalse();
        occurrence.Reminder.NeedsAttention.Should().BeFalse();
        occurrence.Reminder.Attempt.Should().Be(0);
    }

    [Fact]
    public void Arming_SchedulesTheFirstFire()
    {
        var occurrence = Armed();

        occurrence.Reminder.NextFireAtUtc.Should().Be(FireAt);
        occurrence.Reminder.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void ArmingWithNothing_LeavesTheOccurrenceWithoutAReminder()
    {
        var occurrence = PendingOccurrence();

        occurrence.ArmReminder(null);

        occurrence.Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void ArmingACompletedOccurrence_IsRefused()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Now);

        var arm = () => occurrence.ArmReminder(FireAt);

        arm.Should().Throw<DomainException>().WithMessage("*pendente*");
    }

    [Fact]
    public void MarkingFired_DoesNotAcknowledge()
    {
        // O teste que sustenta a funcionalidade inteira: mostrar uma
        // notificação não é o usuário ter dado atenção.
        var occurrence = Armed();

        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));

        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
        occurrence.Reminder.NeedsAttention.Should().BeTrue();
        occurrence.Reminder.Attempt.Should().Be(1);
        occurrence.Reminder.NextFireAtUtc.Should().Be(FireAt.AddMinutes(15));
    }

    [Fact]
    public void TheFirstFire_StartsTheWaitingClock()
    {
        var occurrence = Armed();

        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));

        occurrence.Reminder.WaitingSinceUtc.Should().Be(FireAt);
    }

    [Fact]
    public void TheSecondFire_KeepsTheOriginalWaitingSince()
    {
        // "Aguardando há 35 minutos" conta do primeiro aviso, não do último.
        var occurrence = Armed();
        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));

        occurrence.MarkReminderFired(FireAt.AddMinutes(15), FireAt.AddMinutes(30));

        occurrence.Reminder.WaitingSinceUtc.Should().Be(FireAt);
        occurrence.Reminder.Attempt.Should().Be(2);
    }

    [Fact]
    public void Acknowledging_StopsTheReminder()
    {
        var occurrence = Armed();
        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));

        occurrence.AcknowledgeReminder(FireAt.AddMinutes(5), ReminderAcknowledgement.Opened);

        occurrence.Reminder.IsArmed.Should().BeFalse();
        occurrence.Reminder.NeedsAttention.Should().BeFalse();
        occurrence.Reminder.AcknowledgedBy.Should().Be(ReminderAcknowledgement.Opened);
        occurrence.Reminder.AcknowledgedAtUtc.Should().Be(FireAt.AddMinutes(5));
    }

    [Fact]
    public void AcknowledgingTwice_IsRefused()
    {
        var occurrence = Armed();
        occurrence.AcknowledgeReminder(Now, ReminderAcknowledgement.MarkedSeen);

        var again = () => occurrence.AcknowledgeReminder(Now, ReminderAcknowledgement.Opened);

        again.Should().Throw<DomainException>().WithMessage("*já foi atendido*");
    }

    [Fact]
    public void Snoozing_ResetsInsistenceButKeepsTheReminderArmed()
    {
        // Adiar é dar atenção, então a escada recomeça — mas o lembrete volta.
        var occurrence = Armed();
        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));
        occurrence.MarkReminderFired(FireAt.AddMinutes(15), FireAt.AddMinutes(30));

        occurrence.SnoozeReminder(FireAt.AddMinutes(45));

        occurrence.Reminder.NextFireAtUtc.Should().Be(FireAt.AddMinutes(45));
        occurrence.Reminder.Attempt.Should().Be(0);
        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
    }

    [Fact]
    public void Snoozing_ClearsTheWaitingClock()
    {
        // Contraintuitivo de propósito: depois de adiar, o alerta seguinte diz
        // "aguardando há 0 minutos", porque adiar *é* atenção.
        var occurrence = Armed();
        occurrence.MarkReminderFired(FireAt, FireAt.AddMinutes(15));

        occurrence.SnoozeReminder(FireAt.AddMinutes(45));

        occurrence.Reminder.WaitingSinceUtc.Should().BeNull();
    }

    [Fact]
    public void SnoozingAnAcknowledgedReminder_IsRefused()
    {
        var occurrence = Armed();
        occurrence.AcknowledgeReminder(Now, ReminderAcknowledgement.MarkedSeen);

        var snooze = () => occurrence.SnoozeReminder(FireAt);

        snooze.Should().Throw<DomainException>().WithMessage("*já foi atendido*");
    }

    [Fact]
    public void Completing_RecordsCompletedAsTheAcknowledgement()
    {
        var occurrence = Armed();

        occurrence.Complete(Now.AddHours(2));

        occurrence.Reminder.AcknowledgedBy.Should().Be(ReminderAcknowledgement.Completed);
        occurrence.Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void CompletingAfterMarkingSeen_KeepsTheFirstAcknowledgement()
    {
        // Concluir não pode falhar só porque o lembrete já tinha sido atendido.
        var occurrence = Armed();
        occurrence.AcknowledgeReminder(Now, ReminderAcknowledgement.MarkedSeen);

        occurrence.Complete(Now.AddHours(2));

        occurrence.Reminder.AcknowledgedBy.Should().Be(ReminderAcknowledgement.MarkedSeen);
        occurrence.Reminder.AcknowledgedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void CompletingATaskThatNeverHadAReminder_RecordsNoAcknowledgement()
    {
        var occurrence = PendingOccurrence();

        occurrence.Complete(Now);

        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
    }

    [Fact]
    public void Cancelling_StopsTheReminder()
    {
        var occurrence = Armed();

        occurrence.Cancel();

        occurrence.Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void Reopening_ClearsTheAcknowledgementAndLeavesItDisarmed()
    {
        var occurrence = Armed();
        occurrence.Complete(Now.AddHours(2));

        occurrence.Reopen();

        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
        occurrence.Reminder.IsArmed.Should().BeFalse();
        occurrence.Reminder.Attempt.Should().Be(0);
    }

    [Fact]
    public void Rescheduling_DisarmsSoTheOldTimeCannotFire()
    {
        var occurrence = Armed();

        occurrence.Reschedule(TaskSchedule.At(new DateOnly(2026, 9, 20), new TimeOnly(9, 0)));

        occurrence.Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void AnOccurrenceThatFiredAndWasNeverAnswered_StillNeedsAttention()
    {
        // Política sem repetição: desarmou depois de avisar, mas o ⚠ continua.
        var occurrence = Armed();

        occurrence.MarkReminderFired(FireAt, nextFireAtUtc: null);

        occurrence.Reminder.IsArmed.Should().BeFalse();
        occurrence.Reminder.NeedsAttention.Should().BeTrue();
    }

    private static TaskOccurrence PendingOccurrence() =>
        TaskItem.Create("Verificar estoque", Now, schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)))
            .Occurrences.Single();

    private static TaskOccurrence Armed()
    {
        var occurrence = PendingOccurrence();
        occurrence.ArmReminder(FireAt);
        return occurrence;
    }
}
