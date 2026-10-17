using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Mobile.Core.Composition;

/// <summary>Um escopo por operação (ADR-012), como no desktop.</summary>
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

/// <summary>
/// Quem mexeu, na auditoria local: o nome do celular. Sem conta no aparelho,
/// é o que identifica de onde veio a mudança.
/// </summary>
internal sealed class DeviceUser(string? deviceName) : ICurrentUser
{
    public string? Name => OperationOrigin.Describe(string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim());
}
