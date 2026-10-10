using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class EfUnitOfWork(MyTaskAppDbContext context) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        // Já dentro de uma: quem abriu é quem fecha. Aninhar não faz sentido no SQLite.
        if (context.Database.CurrentTransaction is not null)
        {
            await work(cancellationToken);
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Sem catch: o dispose sem commit desfaz, e a exceção segue para quem chamou.
        await work(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
