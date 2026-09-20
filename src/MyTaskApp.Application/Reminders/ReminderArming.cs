using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// Liga a política da série ao estado da ocorrência. Mesmo padrão do ADR-010: a
/// Application junta os insumos — inclusive o relógio do usuário, que o domínio
/// não pode ter —, a função pura decide, e o agregado guarda a invariante.
/// </summary>
internal static class ReminderArming
{
    /// <summary>
    /// Arma (ou desarma) o lembrete de uma ocorrência pendente. Não salva: quem
    /// fecha a transação é o caso de uso.
    /// </summary>
    public static void Arm(
        TaskItem task,
        TaskOccurrence occurrence,
        IUserClock clock,
        DateTimeOffset nowUtc)
    {
        var firstFire = ReminderScheduling.FirstFireAt(
            task.Reminder,
            nowUtc,
            ScheduledInstantOf(occurrence, clock));

        occurrence.ArmReminder(firstFire);
    }

    /// <summary>Rearma tudo que ainda está pendente na série.</summary>
    public static void ArmPending(TaskItem task, IUserClock clock, DateTimeOffset nowUtc)
    {
        foreach (var occurrence in task.Occurrences)
        {
            if (occurrence.Status is TaskItemStatus.Pending)
            {
                Arm(task, occurrence, clock, nowUtc);
            }
        }
    }

    /// <summary>
    /// O instante absoluto do horário agendado, ou <c>null</c> quando não há
    /// horário — é a borda do ADR-002, e é aqui que ela acontece.
    /// </summary>
    private static DateTimeOffset? ScheduledInstantOf(TaskOccurrence occurrence, IUserClock clock) =>
        occurrence is { ScheduledDate: { } date, ScheduledTime: { } time }
            ? clock.ToInstant(date, time)
            : null;
}
