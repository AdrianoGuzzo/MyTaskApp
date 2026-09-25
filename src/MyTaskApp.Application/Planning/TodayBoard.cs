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
    IReadOnlyList<TagBadge>? Tags = null,
    /// <summary>
    /// Os agentes de IA abertos para a tarefa, um por ambiente; <c>null</c> =
    /// nenhum (ADR-030, ADR-031). É o que acende o selo na linha.
    /// </summary>
    IReadOnlyList<ActiveAgent>? ActiveAgents = null,
    /// <summary>
    /// Os worktrees prontos da tarefa; <c>null</c> = nenhum (ADR-032). É o que
    /// acende a bolinha — a cor vem depois, do Git.
    /// </summary>
    IReadOnlyList<TaskWorktree>? Worktrees = null);

/// <summary>Um worktree da tarefa, com o nome do repositório para a linha e o balão.</summary>
public sealed record TaskWorktree(
    Guid DevelopmentId,
    string RepositoryName,
    string Branch,
    string SourceBranch,
    string WorktreePath);

/// <summary>
/// Um agente aberto: o nome ("Claude Code") e o ambiente em que roda — o que o
/// menu do selo mostra quando a tarefa tem mais de um.
/// </summary>
public sealed record ActiveAgent(
    Guid? DevelopmentId,
    string AgentName,
    string? RepositoryName = null,
    string? Branch = null);

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
