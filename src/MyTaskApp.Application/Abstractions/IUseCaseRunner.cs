namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Executa um caso de uso em escopo próprio. Os handlers dependem de
/// <c>DbContext</c>, que é scoped, enquanto um ViewModel vive enquanto a janela
/// existir — sem isto o ViewModel precisaria receber o contêiner e virar service
/// locator. Como só expõe "rode este caso de uso", também deixa os ViewModels
/// testáveis sem levantar DI.
/// </summary>
public interface IUseCaseRunner
{
    Task<TResult> RunAsync<THandler, TResult>(
        Func<THandler, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull;

    Task RunAsync<THandler>(
        Func<THandler, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull;
}
