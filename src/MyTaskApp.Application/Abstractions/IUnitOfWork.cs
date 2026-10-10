namespace MyTaskApp.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Roda vários casos de uso do mesmo escopo como uma operação só (ADR-059):
    /// cada um grava o seu <c>SaveChanges</c>, e uma falha no meio desfaz todos.
    /// É o que deixa uma edição parcial vinda do MCP reaproveitar os handlers da
    /// tela — título, prazo e etiquetas — sem que metade fique gravada.
    /// </summary>
    /// <remarks>
    /// O padrão só executa: serve aos dublês de teste, que não têm banco. A
    /// implementação de verdade abre a transação.
    /// </remarks>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        work(cancellationToken);
}
