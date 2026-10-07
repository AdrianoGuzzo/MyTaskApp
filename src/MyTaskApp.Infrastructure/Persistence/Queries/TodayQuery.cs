using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class TodayQuery(MyTaskAppDbContext context) : ITodayQuery
{
    public async Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        // Folga de um dia em cada ponta: o recorte exato por data local depende do
        // fuso do usuário e é decidido no domínio, não no SQL.
        var completedFrom = new DateTimeOffset(
            today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var completedUntil = new DateTimeOffset(
            today.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var occurrences = await context.Occurrences
            .AsNoTracking()
            // Arquivado e na lixeira saem da listagem principal (§1, §4). O
            // filtro e explicito, e nao um query filter global: as areas de
            // arquivados e lixeira precisam justamente do que ele exclui, e um
            // filtro global obrigaria IgnoreQueryFilters() espalhado — inclusive
            // no caminho de restaurar, que passaria a nao encontrar o registro.
            .Where(occurrence => context.Tasks.Any(task =>
                task.Id == occurrence.TaskItemId
                && task.ArchivedAt == null
                && task.DeletedAt == null))
            .Where(occurrence =>
                (occurrence.Status == TaskItemStatus.Pending
                    && occurrence.ScheduledDate != null
                    && occurrence.ScheduledDate <= today)
                // Com prazo, a responsabilidade corre desde já, qualquer que
                // seja a data marcada (ADR-050). Cai no índice parcial do prazo.
                || (occurrence.Status == TaskItemStatus.Pending
                    && occurrence.DeadlineDate != null)
                || (occurrence.Status == TaskItemStatus.Completed
                    && occurrence.CompletedAt >= completedFrom
                    && occurrence.CompletedAt < completedUntil))
            .ToListAsync(cancellationToken);

        return await DescribeAsync(occurrences, cancellationToken);
    }

    public async Task<TodayOccurrenceRow?> FindOccurrenceAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default)
    {
        // Arquivada continua abrindo, e abre só para leitura, porque está
        // concluída. A lixeira não abre: lá a tarefa só se restaura (ADR-020).
        var occurrences = await context.Occurrences
            .AsNoTracking()
            .Where(occurrence => occurrence.Id == occurrenceId
                && context.Tasks.Any(task => task.Id == occurrence.TaskItemId && task.DeletedAt == null))
            .ToListAsync(cancellationToken);

        var rows = await DescribeAsync(occurrences, cancellationToken);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// A linha inteira de cada ocorrência — definição, etiquetas, agentes,
    /// worktrees e tempo —, um lote por assunto, nunca uma consulta por linha.
    /// </summary>
    private async Task<IReadOnlyList<TodayOccurrenceRow>> DescribeAsync(
        List<TaskOccurrence> occurrences,
        CancellationToken cancellationToken)
    {
        if (occurrences.Count == 0)
        {
            return [];
        }

        var taskIds = occurrences.Select(occurrence => occurrence.TaskItemId).Distinct().ToArray();

        var definitions = await context.Tasks
            .AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            // Tipo owned em table-sharing vira coluna simples: sem join novo.
            .Select(task => new
            {
                task.Id,
                task.Title,
                task.Priority,
                task.Description,
                Reminder = task.Reminder,
                task.External,
                task.DeadlineAlerts,
                task.NextAction,
                task.Estimate,
            })
            .ToListAsync(cancellationToken);

        var byId = definitions.ToDictionary(definition => definition.Id);

        // Terceira ida ao banco, qualquer que seja o tamanho da lista:
        // as etiquetas de todos os checklists de uma vez, nunca uma por linha.
        var tagLinks = await context.TaskItemTags
            .AsNoTracking()
            .Where(link => taskIds.Contains(link.TaskItemId))
            .Join(
                context.Tags,
                link => link.TagId,
                tag => tag.Id,
                (link, tag) => new { link.TaskItemId, tag.Id, tag.Name, tag.ColorHex })
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);

        var tagsByTask = tagLinks
            .GroupBy(row => row.TaskItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TagBadge>)group
                    .Select(row => new TagBadge(row.Id, row.Name, row.ColorHex))
                    .ToList());

        // O selo de agente em execução (ADR-030). O status vem do banco, que o
        // monitor mantém em dia com os processos — a lista não consulta o sistema.
        // Um por ambiente (ADR-031), com o repositório para o menu de escolha.
        var agents = await (
                from session in context.AgentSessions.AsNoTracking()
                where taskIds.Contains(session.TaskItemId)
                    && session.Status == AgentSessionStatus.Running
                join development in context.TaskDevelopments.AsNoTracking()
                    on session.TaskDevelopmentId equals (Guid?)development.Id into developments
                from development in developments.DefaultIfEmpty()
                select new
                {
                    session.TaskItemId,
                    session.TaskDevelopmentId,
                    session.ProviderId,
                    session.StartedAt,
                    session.Activity,
                    session.ActivityChangedAt,
                    RepositoryPath = development == null ? null : development.RepositoryPath,
                    Branch = development == null ? null : development.Branch,
                })
            .ToListAsync(cancellationToken);

        var agentsByTask = agents
            .GroupBy(row => row.TaskItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ActiveAgentRow>)group
                    .OrderBy(row => row.StartedAt)
                    .Select(row => new ActiveAgentRow(
                        row.TaskDevelopmentId,
                        row.ProviderId,
                        row.RepositoryPath,
                        row.Branch,
                        row.Activity,
                        row.ActivityChangedAt))
                    .ToList());

        // A bolinha de worktree (ADR-034): só os prontos. Criando, com erro ou
        // removido não é pasta onde haja trabalho a perder.
        var worktrees = await context.TaskDevelopments
            .AsNoTracking()
            .Where(development => taskIds.Contains(development.TaskItemId)
                && development.Status == TaskDevelopmentStatus.Ready)
            .Select(development => new
            {
                development.TaskItemId,
                development.Id,
                development.RepositoryPath,
                development.Branch,
                development.SourceBranch,
                development.SourceTag,
                development.WorktreePath,
                development.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var worktreesByTask = worktrees
            .GroupBy(row => row.TaskItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WorktreeRow>)group
                    .OrderBy(row => row.CreatedAt)
                    // Nascida de uma tag, a branch conta os commits a partir dela
                    // (ADR-043): a ponta da branch de origem pode estar anos à frente.
                    .Select(row => new WorktreeRow(
                        row.Id,
                        row.RepositoryPath,
                        row.Branch,
                        row.SourceTag is null ? row.SourceBranch : GitTag.Prefix + row.SourceTag,
                        row.WorktreePath))
                    .ToList());

        // O tempo trabalhado (ADR-052), num lote só como os anteriores. Soma em
        // C#, e não em SQL: os instantes são ticks atrás de um conversor, e o EF
        // não traduz a subtração. São poucos períodos por linha do quadro.
        var occurrenceIds = occurrences.Select(occurrence => occurrence.Id).ToArray();

        var periods = await context.TimeEntries
            .AsNoTracking()
            .Where(entry => occurrenceIds.Contains(entry.TaskOccurrenceId))
            .Select(entry => new { entry.TaskOccurrenceId, entry.StartedAt, entry.EndedAt })
            .ToListAsync(cancellationToken);

        var loggedByOccurrence = periods
            .Where(period => period.EndedAt != null)
            .GroupBy(period => period.TaskOccurrenceId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(TimeSpan.Zero, (total, period) => total + (period.EndedAt!.Value - period.StartedAt)));

        var timerByOccurrence = periods
            .Where(period => period.EndedAt == null)
            .GroupBy(period => period.TaskOccurrenceId)
            .ToDictionary(group => group.Key, group => group.Min(period => period.StartedAt));

        return occurrences
            .Select(occurrence =>
            {
                var definition = byId[occurrence.TaskItemId];

                return new TodayOccurrenceRow(
                    occurrence.Id,
                    definition.Id,
                    definition.Title,
                    definition.Priority,
                    occurrence.ScheduledDate,
                    occurrence.ScheduledTime,
                    occurrence.Status,
                    occurrence.CompletedAt,
                    occurrence.Reminder.AcknowledgedAtUtc is null
                        ? occurrence.Reminder.WaitingSinceUtc
                        : null,
                    occurrence.Reminder.Attempt,
                    definition.Reminder,
                    definition.Description,
                    occurrence.Position,
                    tagsByTask.GetValueOrDefault(definition.Id),
                    agentsByTask.GetValueOrDefault(definition.Id),
                    worktreesByTask.GetValueOrDefault(definition.Id),
                    definition.External,
                    occurrence.Deadline,
                    definition.DeadlineAlerts,
                    definition.NextAction,
                    definition.Estimate,
                    loggedByOccurrence.GetValueOrDefault(occurrence.Id),
                    timerByOccurrence.TryGetValue(occurrence.Id, out var timerStartedAt) ? timerStartedAt : null);
            })
            .ToList();
    }
}
