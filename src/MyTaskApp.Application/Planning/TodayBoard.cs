using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Planning;

public sealed record TodayTask(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    TaskPriority Priority,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    bool IsLate,
    /// <summary>Ha quanto tempo o checklist espera atencao; <c>null</c> = nao espera.</summary>
    TimeSpan? WaitingForAttention = null,
    int ReminderStep = 0,
    ReminderPolicy? Reminder = null,
    /// <summary>
    /// A anotação livre do checklist, em Markdown. Viaja junto com a linha em
    /// vez de ser buscada ao abrir a janela: o quadro de hoje é de dezenas de
    /// itens, anotações costumam ser curtas, e uma segunda consulta só
    /// para preencher uma tela que o usuário acabou de pedir faria a janela
    /// abrir vazia e preencher depois.
    /// </summary>
    string? Notes = null,
    /// <summary>As etiquetas do checklist; <c>null</c> = nenhuma.</summary>
    IReadOnlyList<TagBadge>? Tags = null);

/// <summary>Tela "Hoje" (§9), já separada em seções mutuamente exclusivas.</summary>
public sealed record TodayBoard(
    DateOnly Date,
    IReadOnlyList<TodayTask> Overdue,
    IReadOnlyList<TodayTask> Now,
    IReadOnlyList<TodayTask> Today,
    IReadOnlyList<TodayTask> Unscheduled,
    IReadOnlyList<TodayTask> Completed)
{
    public int TotalVisible =>
        Overdue.Count + Now.Count + Today.Count + Unscheduled.Count + Completed.Count;

    public int RemainingCount => Overdue.Count + Now.Count + Today.Count + Unscheduled.Count;
}
