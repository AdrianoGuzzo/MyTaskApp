using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class TagQuery(MyTaskAppDbContext context) : ITagQuery
{
    /// <remarks>
    /// Uma consulta só: as contagens viram subconsultas correlatas no SQL,
    /// servidas pelos índices de <c>TaskItemTags.TagId</c> e
    /// <c>TagDirectories (TagId, Alias)</c>.
    /// </remarks>
    public async Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagRow(
                tag.Id,
                tag.Name,
                tag.ColorHex,
                context.TaskItemTags.Count(link => link.TagId == tag.Id),
                context.TagDirectories.Count(directory => directory.TagId == tag.Id)))
            .ToListAsync(cancellationToken);

    public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        Guid tagId,
        CancellationToken cancellationToken = default) =>
        ListDirectoriesAsync(
            context.Tags.Where(tag => tag.Id == tagId),
            cancellationToken);

    public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        ListDirectoriesAsync(
            context.Tags.Where(tag => context.TaskItemTags.Any(
                link => link.TaskItemId == taskId && link.TagId == tag.Id)),
            cancellationToken);

    /// <remarks>
    /// Ordena no cliente: a ordem por alias segue o NOCASE da coluna no SQL,
    /// mas o desempate pelo nome da etiqueta (o mesmo alias em duas etiquetas)
    /// fica mais previsível feito aqui, com as mesmas regras em toda consulta.
    /// </remarks>
    private async Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        IQueryable<Domain.Tags.Tag> tags,
        CancellationToken cancellationToken)
    {
        var rows = await tags
            .AsNoTracking()
            .Join(
                context.TagDirectories,
                tag => tag.Id,
                directory => directory.TagId,
                (tag, directory) => new TagDirectoryRow(
                    directory.Id,
                    tag.Id,
                    tag.Name,
                    tag.ColorHex,
                    directory.Alias,
                    directory.Path,
                    directory.Name,
                    directory.Description,
                    directory.DefaultBranch))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(row => row.Alias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.TagName, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
