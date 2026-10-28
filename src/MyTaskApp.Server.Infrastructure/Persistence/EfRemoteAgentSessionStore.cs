using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using Npgsql;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>As conversas do Claude relatadas pelos agentes (ADR-091), sobre o PostgreSQL.</summary>
internal sealed class EfRemoteAgentSessionStore(ServerDbContext context) : IRemoteAgentSessionStore
{
    private static readonly int[] ClosedStatuses = [(int)RemoteAgentSessionStatus.Stopped, (int)RemoteAgentSessionStatus.Failed];

    public async Task<RemoteAgentSessionUpsert> UpsertAsync(Guid userId, RemoteAgentSessionView session, CancellationToken cancellationToken)
    {
        if (await FindRowAsync(userId, session.SessionId, cancellationToken) is not { } existing)
        {
            context.RemoteAgentSessions.Add(ToRow(userId, session));

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new RemoteAgentSessionUpsert(RemoteAgentSessionUpsertResult.Inserted, session.Version);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Dois relatos juntos da mesma conversa: o outro gravou primeiro; vale a regra da versão.
                context.ChangeTracker.Clear();
                existing = await FindRowAsync(userId, session.SessionId, cancellationToken)
                    ?? throw new InvalidOperationException("A conversa sumiu entre a gravação e a releitura.");
            }
        }

        if (existing.DeviceId != session.DeviceId)
        {
            return new RemoteAgentSessionUpsert(RemoteAgentSessionUpsertResult.OtherDevice, existing.Version);
        }

        if (existing.Version >= session.Version)
        {
            return new RemoteAgentSessionUpsert(RemoteAgentSessionUpsertResult.Stale, existing.Version);
        }

        // O WHERE na versão: dois relatos juntos, só o maior fica.
        var updated = await context.RemoteAgentSessions
            .Where(row => row.UserId == userId && row.SessionId == session.SessionId && row.DeviceId == session.DeviceId && row.Version < session.Version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Version, session.Version)
                    .SetProperty(row => row.TaskId, session.TaskId)
                    .SetProperty(row => row.DevelopmentId, session.DevelopmentId)
                    .SetProperty(row => row.ProfileId, session.ProfileId)
                    .SetProperty(row => row.Model, session.Model)
                    .SetProperty(row => row.Status, (int)session.Status)
                    .SetProperty(row => row.Activity, (int)session.Activity)
                    .SetProperty(row => row.ActivityMessage, session.ActivityMessage)
                    .SetProperty(row => row.CurrentOperationId, session.CurrentOperationId)
                    .SetProperty(row => row.StartedAt, session.StartedAt)
                    .SetProperty(row => row.EndedAt, session.EndedAt)
                    .SetProperty(row => row.UpdatedAt, session.UpdatedAt)
                    .SetProperty(row => row.ReportedAt, session.ReportedAt),
                cancellationToken);

        return updated > 0
            ? new RemoteAgentSessionUpsert(RemoteAgentSessionUpsertResult.Updated, session.Version)
            : new RemoteAgentSessionUpsert(
                RemoteAgentSessionUpsertResult.Stale,
                (await FindRowAsync(userId, session.SessionId, cancellationToken))?.Version ?? session.Version);
    }

    public async Task<RemoteAgentSessionView?> FindAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        await FindRowAsync(userId, sessionId, cancellationToken) is { } row ? ToView(row) : null;

    public async Task<IReadOnlyList<RemoteAgentSessionView>> ListAsync(
        Guid userId,
        Guid? deviceId,
        bool openOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = context.RemoteAgentSessions.AsNoTracking().Where(row => row.UserId == userId);

        if (deviceId is { } device)
        {
            query = query.Where(row => row.DeviceId == device);
        }

        if (openOnly)
        {
            var closed = ClosedStatuses;
            query = query.Where(row => !closed.Contains(row.Status));
        }

        return [.. (await query.OrderByDescending(row => row.UpdatedAt).Take(limit).ToListAsync(cancellationToken)).Select(ToView)];
    }

    public Task TouchAgentAsync(Guid userId, Guid deviceId, DateTimeOffset at, CancellationToken cancellationToken) =>
        context.DeviceCapabilities
            .Where(row => row.UserId == userId && row.DeviceId == deviceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AgentSeenAt, at), cancellationToken);

    public async Task<IReadOnlyList<RemoteAgentPresence>> ListAgentsAsync(Guid userId, CancellationToken cancellationToken) =>
        [
            .. (await context.DeviceCapabilities.AsNoTracking()
                    .Where(row => row.UserId == userId && (row.Roles & (int)DeviceRoles.Executor) != 0)
                    .Select(row => new { row.DeviceId, row.AgentSeenAt })
                    .ToListAsync(cancellationToken))
                .Select(row => new RemoteAgentPresence(row.DeviceId, row.AgentSeenAt)),
        ];

    public Task<int> PurgeAsync(DateTimeOffset reportedBefore, CancellationToken cancellationToken) =>
        context.RemoteAgentSessions.Where(row => row.ReportedAt < reportedBefore).ExecuteDeleteAsync(cancellationToken);

    private Task<RemoteAgentSessionRow?> FindRowAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        context.RemoteAgentSessions.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.SessionId == sessionId, cancellationToken);

    private static RemoteAgentSessionRow ToRow(Guid userId, RemoteAgentSessionView session) => new()
    {
        UserId = userId,
        SessionId = session.SessionId,
        DeviceId = session.DeviceId,
        Version = session.Version,
        TaskId = session.TaskId,
        DevelopmentId = session.DevelopmentId,
        ProfileId = session.ProfileId,
        Model = session.Model,
        Status = (int)session.Status,
        Activity = (int)session.Activity,
        ActivityMessage = session.ActivityMessage,
        CurrentOperationId = session.CurrentOperationId,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        UpdatedAt = session.UpdatedAt,
        ReportedAt = session.ReportedAt,
    };

    private static RemoteAgentSessionView ToView(RemoteAgentSessionRow row) => new(
        row.SessionId,
        row.DeviceId,
        row.Version,
        row.TaskId,
        row.DevelopmentId,
        row.ProfileId,
        row.Model,
        (RemoteAgentSessionStatus)row.Status,
        (RemoteAgentActivity)row.Activity,
        row.ActivityMessage,
        row.CurrentOperationId,
        row.StartedAt,
        row.EndedAt,
        row.UpdatedAt,
        row.ReportedAt);
}
