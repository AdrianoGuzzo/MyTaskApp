using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class TagQuery(MyTaskAppDbContext context) : ITagQuery
{
    /// <remarks>
    /// Uma consulta só: a contagem vira subconsulta correlata no SQL, servida
    /// pelo índice de <c>TaskItemTags.TagId</c>.
    /// </remarks>
    public async Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagRow(
                tag.Id,
                tag.Name,
                tag.ColorHex,
                context.TaskItemTags.Count(link => link.TagId == tag.Id)))
            .ToListAsync(cancellationToken);
}
