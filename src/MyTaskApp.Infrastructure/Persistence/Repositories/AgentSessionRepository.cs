using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class AgentSessionRepository(MyTaskAppDbContext context) : IAgentSessionRepository
{
    public async Task AddAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        await context.AgentSessions.AddAsync(session, cancellationToken);

    public Task<AgentSession?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.AgentSessions.SingleOrDefaultAsync(session => session.Id == id, cancellationToken);

    /// <remarks>
    /// Id v7 cresce com o tempo: ordenar por ele é ordenar por criação, com
    /// desempate garantido — duas sessões no mesmo tick não empatam.
    /// </remarks>
    public Task<AgentSession?> FindLatestForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        context.AgentSessions
            .Where(session => session.TaskItemId == taskId)
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AgentSession?> FindLatestForDevelopmentAsync(Guid developmentId, CancellationToken cancellationToken = default) =>
        context.AgentSessions
            .Where(session => session.TaskDevelopmentId == developmentId)
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentSession>> ListActiveForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        await context.AgentSessions
            .Where(session => session.TaskItemId == taskId
                && (session.Status == AgentSessionStatus.Starting
                    || session.Status == AgentSessionStatus.Running))
            .OrderBy(session => session.StartedAt)
            .ThenBy(session => session.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentSession>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        await context.AgentSessions
            .Where(session => session.Status == AgentSessionStatus.Starting
                || session.Status == AgentSessionStatus.Running)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentSession>> ListHeadlessAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken = default) =>
        await context.AgentSessions
            .Where(session => session.Mode == AgentSessionMode.Headless
                && (session.Status == AgentSessionStatus.Starting
                    || session.Status == AgentSessionStatus.Running
                    || session.StartedAt >= since
                    || (session.PromptStartedAt != null && session.PromptStartedAt >= since)))
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentSession>> ListConsultationsForTaskAsync(
        Guid taskId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await context.AgentSessions
            .Where(session => session.TaskItemId == taskId && session.Kind == AgentSessionKind.Consultation)
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task<AgentSession?> FindActiveConsultationByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        var hash = tokenHash.ToUpperInvariant();

        return context.AgentSessions
            .Where(session => session.ConsultationTokenHash == hash
                && session.Kind == AgentSessionKind.Consultation
                && (session.Status == AgentSessionStatus.Starting || session.Status == AgentSessionStatus.Running))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
