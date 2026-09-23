using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class TagRepository(MyTaskAppDbContext context) : ITagRepository
{
    public async Task AddAsync(Tag tag, CancellationToken cancellationToken = default) =>
        await context.Tags.AddAsync(tag, cancellationToken);

    public Task<Tag?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Tags.SingleOrDefaultAsync(tag => tag.Id == id, cancellationToken);

    /// <remarks>
    /// A coluna é NOCASE, então a igualdade aqui já ignora maiúsculas — no
    /// alfabeto ASCII, como o índice único que ela antecipa.
    /// </remarks>
    public Task<bool> NameExistsAsync(
        string name,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default) =>
        context.Tags.AnyAsync(
            tag => tag.Name == name && tag.Id != exceptId,
            cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> FindExistingIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await context.Tags
            .Where(tag => ids.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToListAsync(cancellationToken);
    }

    public void Remove(Tag tag) => context.Tags.Remove(tag);
}
