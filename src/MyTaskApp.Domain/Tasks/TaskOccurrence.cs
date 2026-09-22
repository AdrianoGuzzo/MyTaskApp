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
    /// A casa que o usuário deu a esta ocorrência dentro da seção da tela "Hoje".
    /// <c>null</c> = nunca foi arrastada, e a seção a ordena pelo critério de
    /// sempre (ADR-022).
    /// </summary>
    public int? Position { get; private set; }

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

        // Pelo mesmo motivo: a posição era um lugar na fila de outro dia, e essa
        // fila não é mais a desta ocorrência (ADR-022).
        Position = null;
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

        // Position sobrevive de propósito: reabrir devolve a linha exatamente
        // onde o usuário a tinha posto (ADR-022).
    }

    /// <summary>
    /// Põe a ocorrência na n-ésima casa da seção. <c>internal</c> como
    /// <see cref="DisarmReminder"/>: o único caminho de entrada é a raiz.
    /// </summary>
    /// <remarks>
    /// Só pendente se reordena. A seção CONCLUÍDAS ordena pela data de conclusão
    /// e ignora <see cref="Position"/> — deixar a posição editável ali seria uma
    /// escrita que a leitura descarta em silêncio, e a lista passaria a mentir
    /// sobre a ordem em que as coisas foram feitas. Recusar congela a posição de
    /// quem concluiu, que é o que faz reabrir devolver o lugar (ADR-022).
    /// </remarks>
    internal void PlaceAt(int position)
    {
        EnsureCanBePlaced();

        if (position < 0)
        {
            throw new DomainException("A posição não pode ser negativa.");
        }

        Position = position;
    }

    /// <summary>
    /// Confere que esta ocorrência aceita ser reordenada, sem alterá-la.
    /// </summary>
    /// <remarks>
    /// Existe separado de <see cref="PlaceAt"/> porque reordenar é uma escrita
    /// em <b>várias</b> ocorrências de uma vez: quem coordena precisa conferir
    /// todas antes de mexer em qualquer uma, ou uma recusa no meio da seção
    /// deixaria metade da lista renumerada em memória — a mesma armadilha que a
    /// "Edição atômica" de <see cref="TaskItem.Update"/> evita.
    /// </remarks>
    internal void EnsureCanBePlaced()
    {
        if (Status is not TaskItemStatus.Pending)
        {
            throw new DomainException("Só é possível reordenar uma ocorrência pendente.");
        }
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
    /// Cala o lembrete sem mexer no status da ocorrência. É o que a raiz faz ao
    /// arquivar ou mandar para a lixeira: o checklist saiu da lista principal, e
    /// continuar cobrando atenção por ele seria o app insistindo por algo que o
    /// usuário acabou de mandar guardar. Rearmar é decisão de quem restaura.
    /// </summary>
    internal void DisarmReminder() => Reminder.Disarm();

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
