using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Acesso ao histórico dos comandos rápidos (ADR-051). Estreita por necessidade (ADR-005).</summary>
public interface ICommandExecutionRepository
{
    Task AddAsync(CommandExecution execution, CancellationToken cancellationToken = default);

    Task<CommandExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Todas as que ainda podem ter processo vivo (Queued/Running).</summary>
    Task<IReadOnlyList<CommandExecution>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>As do ambiente, da mais nova para a mais antiga. O histórico é curto: tem teto.</summary>
    Task<IReadOnlyList<CommandExecution>> ListForDevelopmentAsync(
        Guid developmentId,
        CancellationToken cancellationToken = default);

    void Remove(CommandExecution execution);
}
