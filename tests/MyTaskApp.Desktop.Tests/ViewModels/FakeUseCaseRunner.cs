using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Registra qual caso de uso o ViewModel pediu, sem construir handlers reais.
/// O que cada handler faz já é coberto em Application e Infrastructure; aqui
/// interessa saber se a UI pediu o caso de uso certo e reagiu ao resultado.
/// </summary>
internal sealed class FakeUseCaseRunner : IUseCaseRunner
{
    public object? Result { get; set; }

    public List<Type> Invoked { get; } = [];

    /// <summary>Falha aplicada à próxima chamada e então descartada.</summary>
    public Exception? NextFailure { get; set; }

    public Type? LastInvoked => Invoked.Count == 0 ? null : Invoked[^1];

    public Task<TResult> RunAsync<THandler, TResult>(
        Func<THandler, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        Invoked.Add(typeof(THandler));

        if (TakeFailure() is { } failure)
        {
            return Task.FromException<TResult>(failure);
        }

        return Result is TResult result
            ? Task.FromResult(result)
            : throw new InvalidOperationException("Nenhum resultado configurado.");
    }

    public Task RunAsync<THandler>(
        Func<THandler, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        Invoked.Add(typeof(THandler));

        return TakeFailure() is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    private Exception? TakeFailure()
    {
        var failure = NextFailure;
        NextFailure = null;
        return failure;
    }
}
