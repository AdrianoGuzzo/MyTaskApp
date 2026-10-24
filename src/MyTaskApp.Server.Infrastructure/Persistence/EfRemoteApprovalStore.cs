using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using Npgsql;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>As aprovações (ADR-086) sobre o PostgreSQL.</summary>
internal sealed class EfRemoteApprovalStore(ServerDbContext context) : IRemoteApprovalStore
{
    /// <summary>Quantas a varredura expira por passada; o resto fica para a próxima.</summary>
    private const int MaxSweepBatch = 500;

    public async Task<bool> TryAddAsync(ApprovalRecord approval, CancellationToken cancellationToken)
    {
        var request = approval.Request;

        context.RemoteApprovals.Add(new RemoteApprovalRow
        {
            ApprovalId = request.ApprovalId,
            UserId = approval.UserId,
            OperationId = request.OperationId,
            TargetDeviceId = approval.TargetDeviceId,
            Kind = (int)request.Kind,
            Request = JsonSerializer.Serialize(request, RemoteJson.Options),
            RequestHash = request.RequestHash,
            ExpiresAt = request.ExpiresAt.ToUniversalTime(),
            CreatedAt = approval.CreatedAt,
            State = (int)approval.State,
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

    public async Task<ApprovalRecord?> FindAsync(Guid userId, Guid approvalId, CancellationToken cancellationToken) =>
        await context.RemoteApprovals.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.ApprovalId == approvalId, cancellationToken) is { } row
            ? ToModel(row)
            : null;

    public async Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        [
            .. (await context.RemoteApprovals.AsNoTracking()
                    .Where(row => row.UserId == userId && row.State == (int)ApprovalState.Pending && row.ExpiresAt > now)
                    .OrderBy(row => row.CreatedAt)
                    .ToListAsync(cancellationToken))
                .Select(ToModel),
        ];

    /// <summary>Só a primeira resposta grava: o <c>WHERE</c> no estado pendente é o uso único.</summary>
    public async Task<bool> TryAnswerAsync(
        Guid approvalId,
        ApprovalState state,
        string response,
        Guid respondedByDeviceId,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        await context.RemoteApprovals
            .Where(row => row.ApprovalId == approvalId && row.State == (int)ApprovalState.Pending && row.ExpiresAt > at)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.State, (int)state)
                    .SetProperty(row => row.Response, response)
                    .SetProperty(row => row.RespondedByDeviceId, respondedByDeviceId)
                    .SetProperty(row => row.RespondedAt, at),
                cancellationToken) > 0;

    public async Task<bool> ExpireAsync(Guid approvalId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.RemoteApprovals
            .Where(row => row.ApprovalId == approvalId && row.State == (int)ApprovalState.Pending && row.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, (int)ApprovalState.Expired), cancellationToken) > 0;

    public async Task<IReadOnlyList<ApprovalRecord>> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await context.RemoteApprovals.AsNoTracking()
            .Where(row => row.State == (int)ApprovalState.Pending && row.ExpiresAt <= now)
            .OrderBy(row => row.ExpiresAt)
            .Take(MaxSweepBatch)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return [];
        }

        var ids = due.Select(row => row.ApprovalId).ToList();

        await context.RemoteApprovals
            .Where(row => ids.Contains(row.ApprovalId) && row.State == (int)ApprovalState.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, (int)ApprovalState.Expired), cancellationToken);

        return [.. due.Select(row => ToModel(row, ApprovalState.Expired))];
    }

    private static ApprovalRecord ToModel(RemoteApprovalRow row) => ToModel(row, (ApprovalState)row.State);

    private static ApprovalRecord ToModel(RemoteApprovalRow row, ApprovalState state) => new()
    {
        UserId = row.UserId,
        TargetDeviceId = row.TargetDeviceId,
        Request = JsonSerializer.Deserialize<ApprovalRequest>(row.Request, RemoteJson.Options)!,
        CreatedAt = row.CreatedAt,
        State = state,
        Response = row.Response,
        RespondedByDeviceId = row.RespondedByDeviceId,
        RespondedAt = row.RespondedAt,
    };
}
