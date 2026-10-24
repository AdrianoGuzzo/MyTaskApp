using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>Os pedidos de pareamento (ADR-087) sobre o PostgreSQL.</summary>
internal sealed class EfRemotePairingStore(ServerDbContext context) : IRemotePairingStore
{
    public async Task AddAsync(PairingRecord pairing, CancellationToken cancellationToken)
    {
        context.RemotePairings.Add(new RemotePairingRow
        {
            PairingId = pairing.PairingId,
            UserId = pairing.UserId,
            ControllerDeviceId = pairing.ControllerDeviceId,
            ControllerFingerprint = pairing.ControllerKey.Fingerprint,
            ControllerSubjectPublicKeyInfo = pairing.ControllerKey.SubjectPublicKeyInfo,
            ExecutorDeviceId = pairing.ExecutorDeviceId,
            ExecutorFingerprint = pairing.ExecutorKeyFingerprint,
            Nonce = pairing.Nonce,
            CreatedAt = pairing.CreatedAt,
            ExpiresAt = pairing.ExpiresAt,
            ApprovedAt = pairing.ApprovedAt,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PairingRecord?> FindAsync(Guid userId, Guid pairingId, CancellationToken cancellationToken) =>
        await context.RemotePairings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.PairingId == pairingId, cancellationToken) is { } row
            ? ToModel(row)
            : null;

    public Task<int> CountOpenAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.RemotePairings.CountAsync(row => row.UserId == userId && row.ApprovedAt == null && row.ExpiresAt > now, cancellationToken);

    public async Task<IReadOnlyList<PairingRecord>> ListOpenForExecutorAsync(
        Guid userId,
        Guid executorDeviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        [
            .. (await context.RemotePairings.AsNoTracking()
                    .Where(row => row.UserId == userId && row.ExecutorDeviceId == executorDeviceId && row.ApprovedAt == null && row.ExpiresAt > now)
                    .ToListAsync(cancellationToken))
                .Select(ToModel),
        ];

    /// <summary>Só o primeiro aprova: aprovar de novo não muda a hora.</summary>
    public Task ApproveAsync(Guid userId, Guid pairingId, DateTimeOffset at, CancellationToken cancellationToken) =>
        context.RemotePairings
            .Where(row => row.UserId == userId && row.PairingId == pairingId && row.ApprovedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ApprovedAt, at), cancellationToken);

    private static PairingRecord ToModel(RemotePairingRow row) => new()
    {
        PairingId = row.PairingId,
        UserId = row.UserId,
        ControllerDeviceId = row.ControllerDeviceId,
        ControllerKey = new DevicePublicKey(DevicePublicKey.Es256, row.ControllerSubjectPublicKeyInfo, row.ControllerFingerprint),
        ExecutorDeviceId = row.ExecutorDeviceId,
        ExecutorKeyFingerprint = row.ExecutorFingerprint,
        Nonce = row.Nonce,
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
        ApprovedAt = row.ApprovedAt,
    };
}
