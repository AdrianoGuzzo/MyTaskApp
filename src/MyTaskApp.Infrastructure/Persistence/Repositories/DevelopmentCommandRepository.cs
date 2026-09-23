using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class DevelopmentCommandRepository(MyTaskAppDbContext context) : IDevelopmentCommandRepository
{
    public async Task AddAsync(DevelopmentCommand command, CancellationToken cancellationToken = default) =>
        await context.DevelopmentCommands.AddAsync(command, cancellationToken);

    public Task<DevelopmentCommand?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.DevelopmentCommands.SingleOrDefaultAsync(command => command.Id == id, cancellationToken);

    /// <remarks>A coluna é NOCASE: a ordem já não separa maiúsculas.</remarks>
    public async Task<IReadOnlyList<DevelopmentCommand>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.DevelopmentCommands
            .OrderBy(command => command.Alias)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// A coluna é NOCASE, então a igualdade aqui já ignora maiúsculas — como o
    /// índice único que ela antecipa.
    /// </remarks>
    public Task<bool> AliasExistsAsync(
        string alias,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default) =>
        context.DevelopmentCommands.AnyAsync(
            command => command.Alias == alias && command.Id != exceptId,
            cancellationToken);

    public void Remove(DevelopmentCommand command) => context.DevelopmentCommands.Remove(command);
}
