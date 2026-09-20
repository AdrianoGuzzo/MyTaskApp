using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Desktop.Composition;

internal sealed class ScopedUseCaseRunner(IServiceScopeFactory scopeFactory) : IUseCaseRunner
{
    public async Task<TResult> RunAsync<THandler, TResult>(
        Func<THandler, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        return await operation(
            scope.ServiceProvider.GetRequiredService<THandler>(),
            cancellationToken);
    }

    public async Task RunAsync<THandler>(
        Func<THandler, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        await operation(
            scope.ServiceProvider.GetRequiredService<THandler>(),
            cancellationToken);
    }
}
