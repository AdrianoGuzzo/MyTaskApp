using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.External;
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
    /// Os worktrees prontos da tarefa; <c>null</c> = nenhum (ADR-034). É o que
    /// acende a bolinha — a cor vem depois, do Git.
    /// </summary>
    IReadOnlyList<TaskWorktree>? Worktrees = null,
    /// <summary>
    /// A issue vinculada, como estava na última leitura (ADR-045). Vem do
    /// banco, nunca da rede: a lista desenha a chave e o tipo mesmo sem Jira.
    /// </summary>
    ExternalLink? External = null,
    /// <summary>
    /// O prazo já avaliado e escrito; <c>null</c> = sem prazo (ADR-050). A
    /// lista se recarrega a cada minuto, e é isso que faz "5 dias" virar
    /// "4 dias e 23 horas" sem timer nenhum por tarefa.
    /// </summary>
    TaskDeadlineView? Deadline = null,
    /// <summary>Os avisos de prazo da tarefa; <c>null</c> = segue o padrão global.</summary>
    DeadlineAlertStage? DeadlineAlerts = null,
    /// <summary>O próximo passo de uma tarefa longa (§14).</summary>
    string? NextAction = null,
    /// <summary>A estimativa de trabalho (§15).</summary>
    TimeSpan? Estimate = null,
    /// <summary>
    /// O tempo registrado: a soma dos períodos encerrados (ADR-052). Não muda a
    /// cada segundo — o que corre está em <see cref="TimerStartedAt"/>.
    /// </summary>
    TimeSpan Logged = default,
    /// <summary>
    /// O início do cronômetro que corre nesta ocorrência; <c>null</c> = parado.
    /// O relógio da linha é <c>agora − isto</c>, calculado na tela.
    /// </summary>
    DateTimeOffset? TimerStartedAt = null);

/// <summary>Um worktree da tarefa, com o nome do repositório para a linha e o balão.</summary>
public sealed record TaskWorktree(
    Guid DevelopmentId,
    string RepositoryName,
    string Branch,
    string SourceBranch,
    string WorktreePath);

/// <summary>
/// Um agente aberto: o nome ("Claude Code") e o ambiente em que roda — o que o
/// menu do selo mostra quando a tarefa tem mais de um — e o que ele está
/// fazendo, segundo os hooks (ADR-037). <see cref="ActivityChangedAt"/> é o
/// que distingue uma pendência nova de uma que o usuário já viu.
/// </summary>
public sealed record ActiveAgent(
    Guid? DevelopmentId,
    string AgentName,
    string? RepositoryName = null,
    string? Branch = null,
    AgentActivity Activity = AgentActivity.Unknown,
    DateTimeOffset? ActivityChangedAt = null);

/// <summary>Tela "Hoje" (§9), já separada em seções mutuamente exclusivas.</summary>
public sealed record TodayBoard(
    DateOnly Date,
    IReadOnlyList<TodayTask> Overdue,
    IReadOnlyList<TodayTask> Now,
    IReadOnlyList<TodayTask> Today,
    IReadOnlyList<TodayTask> Unscheduled,
    IReadOnlyList<TodayTask> Completed)
{
    /// <summary>
    /// PRAZOS (ADR-050): com prazo e fora do plano de hoje, do prazo mais
    /// próximo ao mais distante. Propriedade, e não parâmetro, para quem monta
    /// um quadro sem prazos não precisar saber que a seção existe.
    /// </summary>
    public IReadOnlyList<TodayTask> Deadlines { get; init; } = [];

    /// <summary>
    /// O cronômetro que corre no app, esteja a tarefa no quadro ou não (ADR-052):
    /// uma tarefa marcada para amanhã também pode estar sendo trabalhada hoje, e
    /// o HUD esconde parte das seções. <c>null</c> = nenhum.
    /// </summary>
    public ActiveTimerView? ActiveTimer { get; init; }

    public int TotalVisible =>
        Overdue.Count + Now.Count + Today.Count + Deadlines.Count + Unscheduled.Count + Completed.Count;

    /// <summary>Tarefa com prazo é trabalho aberto, então PRAZOS conta.</summary>
    public int RemainingCount =>
        Overdue.Count + Now.Count + Today.Count + Deadlines.Count + Unscheduled.Count;

    /// <summary>O resumo de prazos do §11, contado sobre todas as seções abertas.</summary>
    public DeadlineSummary DeadlineSummary => DeadlineSummary.Of(
        Overdue.Concat(Now).Concat(Today).Concat(Deadlines).Concat(Unscheduled));
}

/// <summary>
/// Quantos prazos pedem atenção (§11): atrasados, vencendo hoje e nos próximos
/// sete dias. Um resumo, não um painel.
/// </summary>
public sealed record DeadlineSummary(int Overdue, int DueToday, int ThisWeek)
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public bool IsEmpty => Overdue + DueToday + ThisWeek == 0;

    /// <summary>"1 atrasada · 2 hoje · 3 na semana", só com o que houver.</summary>
    public string Label => string.Join(
        " · ",
        new[]
        {
            Overdue > 0 ? $"{Overdue} {(Overdue == 1 ? "atrasada" : "atrasadas")}" : null,
            DueToday > 0 ? $"{DueToday} hoje" : null,
            ThisWeek > 0 ? $"{ThisWeek} na semana" : null,
        }.OfType<string>());

    public static DeadlineSummary Of(IEnumerable<TodayTask> open)
    {
        var deadlines = open
            .Select(task => task.Deadline)
            .OfType<TaskDeadlineView>()
            .ToList();

        return new DeadlineSummary(
            deadlines.Count(view => view.Status is DeadlineStatus.Overdue),
            deadlines.Count(view => view.Status is DeadlineStatus.DueToday),
            deadlines.Count(view => view.Status is DeadlineStatus.DueSoon or DeadlineStatus.OnTrack
                && view.Remaining <= Week));
    }
}
