using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class EfUnitOfWork(MyTaskAppDbContext context) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
