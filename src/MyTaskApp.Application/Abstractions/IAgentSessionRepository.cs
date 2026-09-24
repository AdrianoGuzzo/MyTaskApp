using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Acesso às sessões de agente (ADR-030). Estreita por necessidade (ADR-005).</summary>
public interface IAgentSessionRepository
{
    Task AddAsync(AgentSession session, CancellationToken cancellationToken = default);

    Task<AgentSession?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A sessão mais recente da tarefa, ativa ou não; <c>null</c> se nunca houve.</summary>
    Task<AgentSession?> FindLatestForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>A sessão mais recente do ambiente (ADR-031), ativa ou não; <c>null</c> se nunca houve.</summary>
    Task<AgentSession?> FindLatestForDevelopmentAsync(Guid developmentId, CancellationToken cancellationToken = default);

    /// <summary>As sessões ativas da tarefa, uma por ambiente, da mais antiga à mais nova.</summary>
    Task<IReadOnlyList<AgentSession>> ListActiveForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>Todas as que ainda podem ter processo vivo (Starting/Running).</summary>
    Task<IReadOnlyList<AgentSession>> ListActiveAsync(CancellationToken cancellationToken = default);
}
