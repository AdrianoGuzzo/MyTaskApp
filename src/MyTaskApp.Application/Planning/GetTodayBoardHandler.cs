using Microsoft.Extensions.Options;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Planning;

/// <summary>
/// Monta a tela "Hoje": busca as candidatas, deixa o domínio decidir a seção de
/// cada uma e ordena cada seção pelo critério que faz sentido para ela.
/// </summary>
public sealed class GetTodayBoardHandler(
    ITodayQuery query,
    IUserClock clock,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options,
    // Opcional para os testes do quadro não precisarem montar agentes: sem
    // catálogo, o selo mostra o id do agente em vez do nome (ADR-030).
    IAgentCliProviders? agents = null,
    // Opcional pelo mesmo motivo: sem ele, o quadro não mostra a faixa do
    // cronômetro ativo, e o resto fica igual (ADR-052).
    IActiveTimerQuery? timers = null)
{
    public async Task<TodayBoard> HandleAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var now = clock.CurrentTime;
        var window = options.Value.ToNowWindow();

        var nowUtc = timeProvider.GetUtcNow();

        var rows = await query.GetCandidatesAsync(today, cancellationToken);

        var placed = rows
            .Select(row => (Row: row, Placement: Place(row, today, now, window)))
            .Where(entry => entry.Placement is not null)
            .ToList();

        TodayTask ToTask((TodayOccurrenceRow Row, TodayPlacement? Placement) entry) =>
            Project(entry, nowUtc) with
            {
                ActiveAgents = Agents(entry.Row.ActiveAgents),
                Worktrees = Worktrees(entry.Row.Worktrees),
                External = entry.Row.External,
                Deadline = entry.Row.Deadline is { } deadline
                    ? TaskDeadlineView.Describe(
                        deadline,
                        clock,
                        nowUtc,
                        entry.Row.Status is TaskItemStatus.Completed ? entry.Row.CompletedAt : null)
                    : null,
                DeadlineAlerts = entry.Row.DeadlineAlerts,
                NextAction = entry.Row.NextAction,
                Estimate = entry.Row.Estimate,
                Logged = entry.Row.Logged,
                TimerStartedAt = entry.Row.TimerStartedAt,
            };

        var activeTimer = timers is null ? null : await timers.FindAsync(cancellationToken);

        IEnumerable<(TodayOccurrenceRow Row, TodayPlacement? Placement)> InSection(TodaySection section) =>
            placed.Where(entry => entry.Placement!.Section == section);

        return new TodayBoard(
            Date: today,
            Overdue: InSection(TodaySection.Overdue)
                .OrderBy(entry => Slot(entry.Row))
                .ThenBy(entry => entry.Row.ScheduledDate)
                .ThenBy(entry => entry.Row.ScheduledTime ?? TimeOnly.MinValue)
                .Select(ToTask)
                .ToList(),
            Now: InSection(TodaySection.Now)
                .OrderBy(entry => Slot(entry.Row))
                .ThenBy(entry => entry.Row.ScheduledTime)
                .Select(ToTask)
                .ToList(),
            Today: InSection(TodaySection.Today)
                .OrderBy(entry => Slot(entry.Row))
                .ThenBy(entry => entry.Row.ScheduledTime)
                .Select(ToTask)
                .ToList(),
            Unscheduled: InSection(TodaySection.Unscheduled)
                .OrderBy(entry => Slot(entry.Row))
                .ThenByDescending(entry => entry.Row.Priority)
                .ThenBy(entry => entry.Row.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToTask)
                .ToList(),

            // CONCLUÍDAS não olha a posição de propósito: ali a ordem é a da
            // conclusão, e nada mais. Deixar a posição mandar faria a lista
            // mentir sobre a ordem em que as coisas foram feitas (ADR-022).
            Completed: InSection(TodaySection.Completed)
                .OrderByDescending(entry => entry.Row.CompletedAt)
                .Select(ToTask)
                .ToList())
        {
            // PRAZOS é ordenada pelo prazo, e nada mais: a pergunta da seção é
            // "o que vence primeiro", e arrastar não muda a resposta.
            Deadlines = InSection(TodaySection.Deadlines)
                .OrderBy(entry => entry.Row.Deadline!.Date)
                .ThenBy(entry => entry.Row.Deadline!.Time)
                .ThenByDescending(entry => entry.Row.Priority)
                .Select(ToTask)
                .ToList(),
            ActiveTimer = activeTimer,
        };
    }

    /// <summary>
    /// A casa escolhida à mão, ou o fim da fila para quem nunca foi arrastado.
    /// É o que faz uma atualização não renumerar a lista de ninguém: com
    /// <c>Position</c> nula em todo mundo, o desempate seguinte é o critério de
    /// sempre e a seção sai exatamente como saía antes (ADR-022).
    /// </summary>
    private static int Slot(TodayOccurrenceRow row) => row.Position ?? int.MaxValue;

    private TodayPlacement? Place(
        TodayOccurrenceRow row,
        DateOnly today,
        TimeOnly now,
        NowWindow window) =>
        TodayClassifier.Classify(
            new TodayCandidate(
                row.ScheduledDate,
                row.ScheduledTime,
                row.Status,
                // O domínio raciocina em data local; a conversão é daqui (ADR-002).
                row.CompletedAt is { } completedAt ? clock.ToLocalDate(completedAt) : null,
                row.Deadline),
            today,
            now,
            window);

    private IReadOnlyList<ActiveAgent>? Agents(IReadOnlyList<ActiveAgentRow>? rows) =>
        rows is null or { Count: 0 }
            ? null
            : rows
                .Select(row => new ActiveAgent(
                    row.DevelopmentId,
                    agents?.Find(row.ProviderId)?.Name ?? row.ProviderId,
                    RepositoryName(row.RepositoryPath),
                    row.Branch,
                    row.Activity,
                    row.ActivityChangedAt))
                .ToList();

    private static IReadOnlyList<TaskWorktree>? Worktrees(IReadOnlyList<WorktreeRow>? rows) =>
        rows is null or { Count: 0 }
            ? null
            : rows
                .Select(row => new TaskWorktree(
                    row.DevelopmentId,
                    RepositoryName(row.RepositoryPath) ?? row.RepositoryPath,
                    row.Branch,
                    row.SourceBranch,
                    row.WorktreePath))
                .ToList();

    /// <summary>"ecossistema-core" de <c>C:\Projetos\ecossistema-core</c>.</summary>
    internal static string? RepositoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length == 0 ? path : name;
    }

    private static TodayTask Project(
        (TodayOccurrenceRow Row, TodayPlacement? Placement) entry,
        DateTimeOffset nowUtc)
    {
        var row = entry.Row;

        // Concluida nao espera mais nada, por mais que tenha esperado antes.
        var waiting = entry.Placement!.Section is not TodaySection.Completed
            && row.ReminderWaitingSinceUtc is { } since
                ? nowUtc - since
                : (TimeSpan?)null;

        return new TodayTask(
            row.OccurrenceId,
            row.TaskId,
            row.Title,
            row.Priority,
            row.ScheduledDate,
            row.ScheduledTime,
            entry.Placement!.IsLate,
            waiting,
            waiting is null
                ? 0
                : ReminderEscalation.LevelFor(
                    row.ReminderAttempt,
                    row.TaskReminder?.Channels ?? AlertChannels.All).Step,
            row.TaskReminder,
            row.Description,
            row.Tags);
    }
}
