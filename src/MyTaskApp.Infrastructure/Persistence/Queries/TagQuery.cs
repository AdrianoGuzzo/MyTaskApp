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

    /// <remarks>Ordena no cliente: o EF não traduz ordenação depois da projeção no record.</remarks>
    public async Task<IReadOnlyList<TagDirectoryCommandRow>> ListDirectoryCommandsAsync(
        Guid directoryId,
        CancellationToken cancellationToken = default)
    {
        var rows = await CommandRows(context.TagDirectoryCommands.Where(binding => binding.TagDirectoryId == directoryId))
            .ToListAsync(cancellationToken);

        return [.. rows.OrderBy(row => row.Order)];
    }

    /// <remarks>
    /// Duas consultas, e o encaixe no cliente: os diretórios com comando são
    /// poucos, e cada botão precisa do texto do comando global.
    /// </remarks>
    public async Task<IReadOnlyList<CommandDirectoryRow>> ListCommandDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var commands = await CommandRows(context.TagDirectoryCommands).ToListAsync(cancellationToken);

        if (commands.Count == 0)
        {
            return [];
        }

        var directoryIds = commands.Select(row => row.DirectoryId).Distinct().ToList();

        var directories = await context.TagDirectories
            .AsNoTracking()
            .Where(directory => directoryIds.Contains(directory.Id))
            .Join(
                context.Tags,
                directory => directory.TagId,
                tag => tag.Id,
                (directory, tag) => new { directory.Id, directory.TagId, TagName = tag.Name, directory.Alias, directory.Path })
            .ToListAsync(cancellationToken);

        return
        [
            .. directories
                .OrderBy(directory => directory.TagName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(directory => directory.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(directory => new CommandDirectoryRow(
                    directory.Id,
                    directory.TagId,
                    directory.TagName,
                    directory.Alias,
                    directory.Path,
                    [.. commands.Where(row => row.DirectoryId == directory.Id).OrderBy(row => row.Order)])),
        ];
    }

    private IQueryable<TagDirectoryCommandRow> CommandRows(IQueryable<Domain.Tags.TagDirectoryCommand> bindings) =>
        bindings
            .AsNoTracking()
            .Join(
                context.DevelopmentCommands,
                binding => binding.DevelopmentCommandId,
                command => command.Id,
                (binding, command) => new TagDirectoryCommandRow(
                    binding.Id,
                    binding.TagDirectoryId,
                    command.Id,
                    command.Alias,
                    command.Name,
                    command.Command,
                    binding.Order,
                    binding.IsEnabled,
                    binding.CommandOverride,
                    binding.WorkingDirectoryOverride));

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
                    directory.DefaultBranch,
                    context.TagDirectoryCommands.Count(binding => binding.TagDirectoryId == directory.Id)))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(row => row.Alias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.TagName, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
