using Microsoft.Extensions.Options;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Reminders;

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
    IAgentCliProviders? agents = null)
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
            Project(entry, nowUtc) with { ActiveAgentName = AgentName(entry.Row.ActiveAgentProviderId) };

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
                .ToList());
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
                row.CompletedAt is { } completedAt ? clock.ToLocalDate(completedAt) : null),
            today,
            now,
            window);

    private string? AgentName(string? providerId) =>
        providerId is null ? null : agents?.Find(providerId)?.Name ?? providerId;

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
