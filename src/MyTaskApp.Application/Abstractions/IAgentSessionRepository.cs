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

    /// <summary>
    /// As conversas sem terminal (ADR-089) ativas ou com prompt desde
    /// <paramref name="since"/>, as mais novas primeiro: o que o agente relata ao servidor (ADR-091).
    /// </summary>
    Task<IReadOnlyList<AgentSession>> ListHeadlessAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken = default);

    /// <summary>As Consultas Inteligentes da tarefa (ADR-081), da mais nova à mais antiga.</summary>
    Task<IReadOnlyList<AgentSession>> ListConsultationsForTaskAsync(
        Guid taskId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>A consulta ativa cujo segredo do MCP tem este hash; <c>null</c> se nenhuma.</summary>
    Task<AgentSession?> FindActiveConsultationByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);
}
