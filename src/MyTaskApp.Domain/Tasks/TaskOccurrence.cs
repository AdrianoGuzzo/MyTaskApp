using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// Uma execução concreta de uma tarefa: o QUANDO e o estado. É a unidade que o
/// usuário marca como concluída e a que o histórico preserva (ADR-001, §34).
/// </summary>
public sealed class TaskOccurrence
{
    // Exigido pela materialização do EF Core. O domínio sempre usa o construtor
    // com estado — este nunca produz uma ocorrência válida por conta própria.
    private TaskOccurrence()
    {
    }

    internal TaskOccurrence(Guid id, Guid taskItemId, TaskSchedule schedule)
    {
        Id = id;
        TaskItemId = taskItemId;
        ScheduledDate = schedule.Date;
        ScheduledTime = schedule.Time;
        Status = TaskItemStatus.Pending;
    }

    public Guid Id { get; }

    public Guid TaskItemId { get; }

    public DateOnly? ScheduledDate { get; private set; }

    public TimeOnly? ScheduledTime { get; private set; }

    public TaskItemStatus Status { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// O estado do lembrete desta ocorrência. Nasce desarmado; quem arma é a
    /// Application, que é quem tem a política da série e o relógio do usuário.
    /// </summary>
    public ReminderState Reminder { get; private set; } = new();

    public TaskSchedule Schedule => new(ScheduledDate, ScheduledTime);

    public void Complete(DateTimeOffset completedAt)
    {
        switch (Status)
        {
            case TaskItemStatus.Completed:
                throw new DomainException("Esta ocorrência já foi concluída.");
            case TaskItemStatus.Cancelled:
                throw new DomainException("Não é possível concluir uma ocorrência cancelada.");
        }

        Status = TaskItemStatus.Completed;
        CompletedAt = completedAt;

        // Concluir é a forma mais forte de dar atenção: o lembrete para junto,
        // sem o usuário precisar dispensá-lo separadamente.
        if (Reminder.NeedsAttention)
        {
            Reminder.Acknowledge(completedAt, ReminderAcknowledgement.Completed);
        }
        else
        {
            Reminder.Disarm();
        }
    }

    public void Reschedule(TaskSchedule schedule)
    {
        if (Status is not TaskItemStatus.Pending)
        {
            throw new DomainException(
                "Só é possível reagendar uma ocorrência pendente.");
        }

        ScheduledDate = schedule.Date;
        ScheduledTime = schedule.Time;

        // O instante antigo não pode mais disparar. Quem rearma é a Application,
        // logo em seguida, com a política em mãos.
        Reminder.Disarm();
    }

    public void Cancel()
    {
        if (Status is not TaskItemStatus.Pending)
        {
            throw new DomainException("Só é possível cancelar uma ocorrência pendente.");
        }

        Status = TaskItemStatus.Cancelled;
        Reminder.Disarm();
    }

    public void Reopen()
    {
        if (Status is TaskItemStatus.Pending)
        {
            throw new DomainException("Esta ocorrência já está pendente.");
        }

        Status = TaskItemStatus.Pending;
        CompletedAt = null;

        // Volta a ser pendente sem lembrete: rearmar é decisão da Application,
        // que sabe a política. Ressuscitar um horário vencido avisaria na hora.
        Reminder.Reset();
    }

    /// <summary>
    /// Agenda o primeiro aviso. <c>null</c> deixa a ocorrência sem lembrete —
    /// é o que acontece com uma política desligada.
    /// </summary>
    public void ArmReminder(DateTimeOffset? nextFireAtUtc)
    {
        if (Status is not TaskItemStatus.Pending)
        {
            throw new DomainException(
                "Só é possível lembrar de uma ocorrência pendente.");
        }

        Reminder.Arm(nextFireAtUtc);
    }

    /// <summary>
    /// O aviso saiu. <b>Não</b> encerra o lembrete: quem encerra é
    /// <see cref="AcknowledgeReminder"/>.
    /// </summary>
    public void MarkReminderFired(DateTimeOffset firedAtUtc, DateTimeOffset? nextFireAtUtc) =>
        Reminder.MarkFired(firedAtUtc, nextFireAtUtc);

    /// <summary>O usuário deu atenção: abriu, marcou como visto ou concluiu.</summary>
    public void AcknowledgeReminder(DateTimeOffset atUtc, ReminderAcknowledgement by)
    {
        if (Reminder.IsAcknowledged)
        {
            throw new DomainException("Este lembrete já foi atendido.");
        }

        Reminder.Acknowledge(atUtc, by);
    }

    /// <summary>Volta a avisar mais tarde, com a insistência zerada.</summary>
    public void SnoozeReminder(DateTimeOffset untilUtc)
    {
        if (Status is not TaskItemStatus.Pending)
        {
            throw new DomainException("Só é possível adiar o lembrete de uma ocorrência pendente.");
        }

        if (Reminder.IsAcknowledged)
        {
            throw new DomainException("Este lembrete já foi atendido.");
        }

        Reminder.Snooze(untilUtc);
    }
}
