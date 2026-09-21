using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class ChecklistArchiveQuery(MyTaskAppDbContext context) : IChecklistArchiveQuery
{
    /// <summary>Barra invertida escapa o curinga que o usuário digitou.</summary>
    private const string LikeEscape = "\\";

    public async Task<IReadOnlyList<ChecklistSummaryRow>> SearchAsync(
        ChecklistScope scope,
        string? search,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.Tasks.AsNoTracking();

        // A lixeira vence o arquivo: um checklist arquivado e depois excluído
        // aparece só na lixeira, que é onde o usuário pode agir sobre ele.
        query = scope is ChecklistScope.Trashed
            ? query.Where(task => task.DeletedAt != null)
            : query.Where(task => task.ArchivedAt != null && task.DeletedAt == null);

        // O recorte usa a data que define a área, e cai no mesmo índice parcial
        // da ordenação logo abaixo.
        if (since is { } from)
        {
            query = scope is ChecklistScope.Trashed
                ? query.Where(task => task.DeletedAt >= from)
                : query.Where(task => task.ArchivedAt >= from);
        }

        if (BuildPattern(search) is { } pattern)
        {
            query = query.Where(task =>
                EF.Functions.Like(task.Title, pattern, LikeEscape)
                || (task.Description != null
                    && EF.Functions.Like(task.Description, pattern, LikeEscape)));
        }

        query = scope is ChecklistScope.Trashed
            ? query.OrderByDescending(task => task.DeletedAt)
            : query.OrderByDescending(task => task.ArchivedAt);

        return await query
            .Select(task => new ChecklistSummaryRow(
                task.Id,
                task.Title,
                task.Description,
                task.Priority,
                task.CreatedAt,
                task.ConcludedAt,
                task.ArchivedAt,
                task.DeletedAt,
                task.DeletedBy,
                task.Occurrences.Count,
                task.Occurrences.Count(
                    occurrence => occurrence.Status == TaskItemStatus.Completed)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Termo vazio devolve <c>null</c> — sem filtro. Os curingas do LIKE são
    /// escapados: quem procura por "50%" quer o texto, não "qualquer coisa
    /// depois de 50".
    /// </summary>
    private static string? BuildPattern(string? search)
    {
        var term = search?.Trim();

        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        var escaped = term
            .Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
