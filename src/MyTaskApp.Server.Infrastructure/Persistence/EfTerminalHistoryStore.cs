using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;

namespace MyTaskApp.Server.Infrastructure.Persistence;

internal sealed class TerminalHistoryRow
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string State { get; set; }
}

internal sealed class EfTerminalHistoryStore(ServerDbContext context) : ITerminalHistoryStore
{
    public async Task IngestAsync(Guid userId, Guid deviceId, TerminalLifecyclePage batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var key = userId.ToString("N") + deviceId.ToString("N");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        foreach (var item in batch.Events.OrderBy(item => item.SessionId).ThenBy(item => item.Sequence))
        {
            var sessionKey = userId.ToString("N") + item.SessionId.ToString("N");
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({sessionKey}, 0))", cancellationToken);
            if (await context.Set<TerminalHistoryRow>().AnyAsync(row => row.UserId == userId && row.SessionId == item.SessionId && row.DeviceId != deviceId, cancellationToken))
                throw SyncServerException.Forbidden("Esta sessão pertence a outra máquina.");
            var existing = await context.Set<TerminalHistoryRow>().SingleOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId && row.SessionId == item.SessionId && row.Sequence == item.Sequence, cancellationToken);
            if (existing is not null)
            {
                if (existing.EventId != item.EventId) throw SyncServerException.BadRequest("A sequência do terminal já pertence a outro evento.");
                var expected = item.State with { ActivityMessage = null, UpdatedAt = item.OccurredAt };
                if (JsonSerializer.Deserialize<RemoteTerminalSessionReport>(existing.State, RemoteJson.Options) != expected)
                    throw SyncServerException.BadRequest("O evento do terminal não pode ser alterado depois de gravado.");
                continue;
            }
            if (await context.Set<TerminalHistoryRow>().AnyAsync(row => row.UserId == userId && row.EventId == item.EventId, cancellationToken))
                throw SyncServerException.BadRequest("O identificador do evento já foi utilizado.");
            var previous = await context.Set<TerminalHistoryRow>().Where(row => row.UserId == userId && row.DeviceId == deviceId && row.SessionId == item.SessionId)
                .OrderByDescending(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
            var before = previous is null ? null : JsonSerializer.Deserialize<RemoteTerminalSessionReport>(previous.State, RemoteJson.Options);
            var safe = item.State with { ActivityMessage = null, UpdatedAt = item.OccurredAt };
            var at = (item.OccurredAt > now ? now : item.OccurredAt).ToUniversalTime();
            context.Add(new TerminalHistoryRow { UserId = userId, DeviceId = deviceId, SessionId = item.SessionId, Sequence = item.Sequence,
                EventId = item.EventId, OccurredAt = at, State = JsonSerializer.Serialize(safe, RemoteJson.Options) });
            if ((previous is null || (previous.Sequence < item.Sequence && (before!.Process != safe.Process || before.Activity != safe.Activity || before.HasDialog != safe.HasDialog))) && at >= now.AddHours(-24) && (safe.Process != RemoteTerminalProcess.Running || safe.HasDialog || safe.Activity is RemoteAgentActivity.WaitingForUser or RemoteAgentActivity.WaitingReview or RemoteAgentActivity.Failed))
            {
                var targets = await context.Set<RemotePushDestinationRow>().Where(row => row.UserId == userId && row.DeviceId != deviceId).ToListAsync(cancellationToken);
                foreach (var target in targets)
                    context.Add(new RemotePushDeliveryRow { EventId = item.EventId, UserId = userId, DeviceId = target.DeviceId, TaskId = safe.TaskId,
                        SessionId = safe.SessionId, Kind = safe.Process == RemoteTerminalProcess.Crashed || safe.Activity == RemoteAgentActivity.Failed ? "failed" :
                            safe.Process == RemoteTerminalProcess.Exited ? "exited" : safe.Activity == RemoteAgentActivity.WaitingReview ? "review" : "waiting",
                        DueAt = now, ExpiresAt = now.AddHours(24) });
            }
            await context.SaveChangesAsync(cancellationToken);
        }
        await context.Set<TerminalHistoryRow>().Where(row => row.OccurredAt < now.AddDays(-30)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TerminalLifecycleCurrentList> ListCurrentAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await context.Set<TerminalHistoryRow>().AsNoTracking()
            .Where(row => row.UserId == userId && row.OccurredAt >= now.AddDays(-30) &&
                !context.Set<TerminalHistoryRow>().Any(next => next.UserId == row.UserId && next.DeviceId == row.DeviceId && next.SessionId == row.SessionId && next.Sequence > row.Sequence))
            .ToListAsync(cancellationToken);
        return new TerminalLifecycleCurrentList(rows.Select(row => new TerminalLifecycleCurrent(new RemoteTerminalSessionView(row.DeviceId,
            JsonSerializer.Deserialize<RemoteTerminalSessionReport>(row.State, RemoteJson.Options)!, row.OccurredAt), row.Sequence, row.EventId)).ToList());
    }

    public async Task<IReadOnlyList<RemoteTerminalSessionView>> ListAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await ListCurrentAsync(userId, now, cancellationToken)).Sessions.Select(item => item.View).ToList();

    public async Task<TerminalLifecyclePage> EventsAsync(Guid userId, Guid sessionId, long after, CancellationToken cancellationToken)
    {
        var rows = await context.Set<TerminalHistoryRow>().AsNoTracking().Where(row => row.UserId == userId && row.SessionId == sessionId && row.Sequence > after)
            .OrderBy(row => row.Sequence).Take(100).ToListAsync(cancellationToken);
        return new TerminalLifecyclePage(rows.Select(row => new TerminalLifecycleEvent(row.EventId, row.DeviceId, row.SessionId, row.Sequence,
            row.OccurredAt, JsonSerializer.Deserialize<RemoteTerminalSessionReport>(row.State, RemoteJson.Options)!)).ToList());
    }
}
