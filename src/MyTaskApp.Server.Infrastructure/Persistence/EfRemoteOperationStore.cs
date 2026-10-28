using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using Npgsql;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>A fila de operações e os eventos delas (ADR-086) sobre o PostgreSQL.</summary>
internal sealed class EfRemoteOperationStore(ServerDbContext context) : IRemoteOperationStore
{
    /// <summary>Quantas operações uma passada da varredura muda; o resto fica para a próxima.</summary>
    private const int MaxSweepBatch = 500;

    private static readonly int[] Started =
    [
        (int)RemoteOperationStatus.Accepted, (int)RemoteOperationStatus.Running, (int)RemoteOperationStatus.WaitingApproval,
    ];

    public async Task<bool> TryAddAsync(RemoteOperationRecord operation, CancellationToken cancellationToken)
    {
        context.RemoteOperations.Add(new RemoteOperationRow
        {
            OperationId = operation.OperationId,
            UserId = operation.UserId,
            WorkspaceId = operation.WorkspaceId,
            TargetDeviceId = operation.TargetDeviceId,
            RequestedByDeviceId = operation.RequestedByDeviceId,
            Kind = operation.Kind,
            TaskId = operation.TaskId,
            Status = (int)operation.Status,

            // O timestamptz do Npgsql só aceita UTC; o controlador assina na hora local dele.
            CreatedAt = operation.CreatedAt.ToUniversalTime(),
            NotAfter = operation.NotAfter.ToUniversalTime(),
            ReceivedAt = operation.ReceivedAt,
            UpdatedAt = operation.UpdatedAt,
            Request = operation.Request,
            RequestHash = operation.RequestHash,
            AgentSessionId = operation.AgentSessionId,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            context.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<RemoteOperationRecord?> FindAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
        await context.RemoteOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.OperationId == operationId, cancellationToken) is { } row
            ? ToModel(row)
            : null;

    public async Task<IReadOnlyList<RemoteOperationRecord>> ListRecentAsync(Guid userId, int limit, CancellationToken cancellationToken) =>
        [
            .. (await context.RemoteOperations.AsNoTracking()
                    .Where(row => row.UserId == userId)
                    .OrderByDescending(row => row.ReceivedAt)
                    .Take(limit)
                    .ToListAsync(cancellationToken))
                .Select(ToModel),
        ];

    public async Task<IReadOnlyList<Guid>> DispatchQueuedAsync(Guid userId, Guid targetDeviceId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // FOR UPDATE: duas buscas juntas do mesmo agente não entregam a mesma duas vezes.
        var ids = await context.Database
            .SqlQuery<Guid>(
                $"SELECT \"OperationId\" AS \"Value\" FROM \"RemoteOperations\" WHERE \"UserId\" = {userId} AND \"TargetDeviceId\" = {targetDeviceId} AND \"Status\" = {(int)RemoteOperationStatus.Queued} AND \"NotAfter\" > {at} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (ids.Count > 0)
        {
            await context.RemoteOperations
                .Where(row => ids.Contains(row.OperationId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, (int)RemoteOperationStatus.Dispatched)
                        .SetProperty(row => row.DispatchedAt, at)
                        .SetProperty(row => row.UpdatedAt, at),
                    cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return ids;
    }

    public Task<int> CountOpenForTargetAsync(Guid userId, Guid targetDeviceId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.RemoteOperations
            .Where(row => row.UserId == userId && row.TargetDeviceId == targetDeviceId)
            .Where(row => ((row.Status == (int)RemoteOperationStatus.Queued || row.Status == (int)RemoteOperationStatus.Dispatched) && row.NotAfter > now)
                || Started.Contains(row.Status))
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<RemoteOperationRecord>> ListActiveForTargetAsync(
        Guid userId,
        Guid targetDeviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        [
            .. (await context.RemoteOperations.AsNoTracking()
                    .Where(row => row.UserId == userId && row.TargetDeviceId == targetDeviceId)
                    .Where(row => (row.Status == (int)RemoteOperationStatus.Dispatched && row.NotAfter > now) || Started.Contains(row.Status))
                    .OrderBy(row => row.ReceivedAt)
                    .ToListAsync(cancellationToken))
                .Select(ToModel),
        ];

    public async Task<bool> TryMoveAsync(
        Guid operationId,
        RemoteOperationStatus from,
        RemoteOperationStatus to,
        DateTimeOffset at,
        string? code,
        string? summary,
        CancellationToken cancellationToken) =>
        await context.RemoteOperations
            .Where(row => row.OperationId == operationId && row.Status == (int)from)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)to)
                    .SetProperty(row => row.UpdatedAt, at)
                    .SetProperty(row => row.ResultCode, row => code ?? row.ResultCode)
                    .SetProperty(row => row.ResultSummary, row => summary ?? row.ResultSummary),
                cancellationToken) > 0;

    public Task RequestCancelAsync(Guid operationId, DateTimeOffset at, CancellationToken cancellationToken) =>
        context.RemoteOperations
            .Where(row => row.OperationId == operationId && row.CancelRequestedAt == null && Started.Contains(row.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.CancelRequestedAt, at)
                    .SetProperty(row => row.UpdatedAt, at),
                cancellationToken);

    public async Task<long> AppendEventsAsync(
        Guid operationId,
        IReadOnlyList<RemoteOperationEvent> events,
        int maxStored,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // SELECT … FOR UPDATE na operação: dois lotes juntos não contam o teto
        // nem a sequência um por cima do outro.
        var locked = await context.Database
            .SqlQuery<Guid>($"SELECT \"OperationId\" AS \"Value\" FROM \"RemoteOperations\" WHERE \"OperationId\" = {operationId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            throw SyncServerException.NotFound();
        }

        var operation = await context.RemoteOperations.SingleAsync(row => row.OperationId == operationId, cancellationToken);
        var seqs = events.Select(item => item.Seq).ToList();
        var stored = (await context.RemoteOperationEvents
                .Where(row => row.OperationId == operationId && seqs.Contains(row.Seq))
                .Select(row => row.Seq)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var discarded = new List<long>();

        foreach (var item in events.Where(item => !stored.Contains(item.Seq)).OrderBy(item => item.Seq))
        {
            if (operation.EventCount >= maxStored && item.Kind == RemoteEventKind.Output)
            {
                discarded.Add(item.Seq);
                continue;
            }

            context.RemoteOperationEvents.Add(new RemoteOperationEventRow
            {
                OperationId = operationId,
                Seq = item.Seq,
                At = item.At.ToUniversalTime(),
                ReceivedAt = at,
                Kind = (int)item.Kind,
                Step = item.Step,
                Status = (int?)item.Status,
                Text = item.Text,
                Data = item.Data?.GetRawText(),
            });

            operation.EventCount++;
        }

        await context.SaveChangesAsync(cancellationToken);

        var above = await context.RemoteOperationEvents
            .Where(row => row.OperationId == operationId && row.Seq > operation.LastContiguousSeq)
            .Select(row => row.Seq)
            .ToListAsync(cancellationToken);

        operation.LastContiguousSeq = RemoteEventSequence.LastContiguous(operation.LastContiguousSeq, above.Concat(discarded));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return operation.LastContiguousSeq;
    }

    public async Task<IReadOnlyList<RemoteOperationEvent>> ListEventsAsync(Guid operationId, long after, int limit, CancellationToken cancellationToken) =>
        [
            .. (await context.RemoteOperationEvents.AsNoTracking()
                    .Where(row => row.OperationId == operationId && row.Seq > after)
                    .OrderBy(row => row.Seq)
                    .Take(limit)
                    .ToListAsync(cancellationToken))
                .Select(row => new RemoteOperationEvent(
                    row.OperationId,
                    row.Seq,
                    row.At,
                    (RemoteEventKind)row.Kind,
                    row.Step,
                    (RemoteOperationStatus?)row.Status,
                    row.Text,
                    row.Data is null ? null : JsonSerializer.Deserialize<JsonElement>(row.Data))),
        ];

    public async Task<IReadOnlyList<RemoteOperationRecord>> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await context.RemoteOperations.AsNoTracking()
            .Where(row => (row.Status == (int)RemoteOperationStatus.Queued || row.Status == (int)RemoteOperationStatus.Dispatched) && row.NotAfter <= now)
            .OrderBy(row => row.NotAfter)
            .Take(MaxSweepBatch)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return [];
        }

        var ids = due.Select(row => row.OperationId).ToList();

        // A mesma condição de novo: a que o agente aceitou no meio do caminho fica.
        await context.RemoteOperations
            .Where(row => ids.Contains(row.OperationId))
            .Where(row => (row.Status == (int)RemoteOperationStatus.Queued || row.Status == (int)RemoteOperationStatus.Dispatched) && row.NotAfter <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)RemoteOperationStatus.Expired)
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken);

        return [.. due.Select(row => ToModel(row, RemoteOperationStatus.Expired))];
    }

    public async Task<IReadOnlyList<RemoteOperationRecord>> RequeueAsync(DateTimeOffset dispatchedBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = await context.RemoteOperations.AsNoTracking()
            .Where(row => row.Status == (int)RemoteOperationStatus.Dispatched && row.DispatchedAt <= dispatchedBefore && row.NotAfter > now)
            .OrderBy(row => row.DispatchedAt)
            .Take(MaxSweepBatch)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return [];
        }

        var ids = stale.Select(row => row.OperationId).ToList();

        await context.RemoteOperations
            .Where(row => ids.Contains(row.OperationId))
            .Where(row => row.Status == (int)RemoteOperationStatus.Dispatched && row.DispatchedAt <= dispatchedBefore)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)RemoteOperationStatus.Queued)
                    .SetProperty(row => row.DispatchedAt, (DateTimeOffset?)null)
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken);

        return [.. stale.Select(row => ToModel(row, RemoteOperationStatus.Queued))];
    }

    public Task<int> PruneEventsAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) =>
        context.RemoteOperationEvents.Where(row => row.ReceivedAt < receivedBefore).ExecuteDeleteAsync(cancellationToken);

    public Task LinkSessionAsync(Guid userId, Guid targetDeviceId, Guid operationId, Guid sessionId, CancellationToken cancellationToken) =>
        context.RemoteOperations
            .Where(row => row.UserId == userId && row.OperationId == operationId && row.TargetDeviceId == targetDeviceId && row.AgentSessionId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AgentSessionId, sessionId), cancellationToken);

    public async Task<IReadOnlyList<RemoteOperationRecord>> ListForSessionAsync(Guid userId, Guid targetDeviceId, Guid sessionId, int limit, CancellationToken cancellationToken) =>
        [
            .. (await context.RemoteOperations.AsNoTracking()
                    .Where(row => row.UserId == userId && row.TargetDeviceId == targetDeviceId && row.AgentSessionId == sessionId)
                    .OrderByDescending(row => row.ReceivedAt)
                    .Take(limit)
                    .ToListAsync(cancellationToken))
                .OrderBy(row => row.ReceivedAt)
                .Select(ToModel),
        ];

    private static RemoteOperationRecord ToModel(RemoteOperationRow row) => ToModel(row, (RemoteOperationStatus)row.Status);

    private static RemoteOperationRecord ToModel(RemoteOperationRow row, RemoteOperationStatus status) => new()
    {
        OperationId = row.OperationId,
        UserId = row.UserId,
        WorkspaceId = row.WorkspaceId,
        TargetDeviceId = row.TargetDeviceId,
        RequestedByDeviceId = row.RequestedByDeviceId,
        Kind = row.Kind,
        TaskId = row.TaskId,
        Status = status,
        CreatedAt = row.CreatedAt,
        NotAfter = row.NotAfter,
        ReceivedAt = row.ReceivedAt,
        UpdatedAt = row.UpdatedAt,
        DispatchedAt = row.DispatchedAt,
        CancelRequestedAt = row.CancelRequestedAt,
        ResultCode = row.ResultCode,
        ResultSummary = row.ResultSummary,
        LastContiguousSeq = row.LastContiguousSeq,
        Request = row.Request,
        RequestHash = row.RequestHash,
        AgentSessionId = row.AgentSessionId,
    };
}
