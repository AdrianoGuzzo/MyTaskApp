using Microsoft.Extensions.DependencyInjection;

namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Um escopo por operação (ADR-012): abre, resolve o handler, executa e
/// descarta. É o executor de todo host — desktop, celular e, na fase 2, o
/// agente sem tela —, e por isso mora aqui, e não na composição de cada um.
/// </summary>
public sealed class ScopedUseCaseRunner(IServiceScopeFactory scopeFactory) : IUseCaseRunner
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
