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

    /// <summary>
    /// Resultado por caso de uso, para quando um comando pede mais de um. Sem
    /// isto, "mover para a lixeira" — que lê o prazo antes de perguntar — não
    /// teria como devolver uma política e um quadro na mesma sequência.
    /// </summary>
    public Dictionary<Type, object> ResultsByHandler { get; } = [];

    public List<Type> Invoked { get; } = [];

    /// <summary>
    /// Handlers de verdade, por tipo. Com um registrado aqui, a operação é
    /// realmente executada — é o que permite conferir o <i>comando</i> que o
    /// ViewModel montou, e não só qual caso de uso ele pediu. Vazio (o padrão),
    /// nada é executado e o fake se comporta como sempre.
    /// </summary>
    public Dictionary<Type, object> Handlers { get; } = [];

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

        if (ResultsByHandler.TryGetValue(typeof(THandler), out var specific)
            && specific is TResult configured)
        {
            return Task.FromResult(configured);
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

        if (TakeFailure() is { } failure)
        {
            return Task.FromException(failure);
        }

        return Handlers.TryGetValue(typeof(THandler), out var handler)
            ? operation((THandler)handler, cancellationToken)
            : Task.CompletedTask;
    }

    private Exception? TakeFailure()
    {
        var failure = NextFailure;
        NextFailure = null;
        return failure;
    }
}
