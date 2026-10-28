using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// O pareamento de um controlador com uma máquina executora (fase 2, T-05;
/// ADR-087, RS-08). O servidor só leva e traz: guarda as duas chaves do momento
/// do pedido e um nonce, e marca aprovado quando o executor assina. Quem confia
/// é o executor, que grava a chave do controlador localmente depois de a
/// pessoa conferir o código na tela dele.
/// </summary>
public sealed class RemotePairingUseCases(
    IDeviceRegistry devices,
    IRemoteDeviceStore remote,
    IRemotePairingStore pairings,
    IRemoteNotifier notifier,
    TimeProvider timeProvider,
    ILogger<RemotePairingUseCases> logger)
{
    /// <summary>
    /// A varredura (fase 3, E9): o pedido de pareamento só serve enquanto não
    /// vence. A confiança fica nos <c>TrustedControllers</c> do executor, não aqui.
    /// </summary>
    public async Task<int> SweepAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var purged = await pairings.PurgeAsync(timeProvider.GetUtcNow() - retention, cancellationToken);

        if (purged > 0)
        {
            logger.LogInformation("RemotePairingsSwept {Purged}", purged);
        }

        return purged;
    }

    /// <summary>O controlador do token pede para controlar <see cref="PairingRequest.ExecutorDeviceId"/>.</summary>
    public async Task<PairingInfo> CreateAsync(SyncCaller caller, PairingRequest request, CancellationToken cancellationToken)
    {
        if (request.ExecutorDeviceId == caller.DeviceId)
        {
            throw SyncServerException.BadRequest("Um dispositivo não pareia consigo mesmo.");
        }

        var controllerKey = await remote.FindActiveKeyAsync(caller.UserId, caller.DeviceId, cancellationToken)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de parear.");

        if (await devices.FindAsync(caller.UserId, request.ExecutorDeviceId, cancellationToken) is not { IsRevoked: false })
        {
            throw SyncServerException.NotFound();
        }

        var executor = (await remote.ListAsync(caller.UserId, cancellationToken))
            .SingleOrDefault(state => state.DeviceId == request.ExecutorDeviceId);

        if (executor is null || !executor.Roles.HasFlag(DeviceRoles.Executor))
        {
            throw new SyncServerException(RemoteErrorCodes.NotAnExecutor, 409, "Essa máquina não aceita controle remoto. Ligue a opção nela primeiro.");
        }

        var now = timeProvider.GetUtcNow();

        if (await pairings.CountOpenAsync(caller.UserId, now, cancellationToken) >= RemoteLimits.MaxOpenPairings)
        {
            throw new SyncServerException(RemoteErrorCodes.TooManyPairings, 409, "Pedidos de pareamento demais em aberto. Espere os antigos expirarem.");
        }

        var pairing = new PairingRecord
        {
            PairingId = Guid.CreateVersion7(),
            UserId = caller.UserId,
            ControllerDeviceId = caller.DeviceId,
            ControllerKey = controllerKey,
            ExecutorDeviceId = request.ExecutorDeviceId,
            ExecutorKeyFingerprint = executor.KeyFingerprint,
            Nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            CreatedAt = now,
            ExpiresAt = now + RemoteLimits.PairingLifetime,
        };

        await pairings.AddAsync(pairing, cancellationToken);
        await notifier.ToDeviceAsync(caller.UserId, pairing.ExecutorDeviceId, RemoteHubMessages.PairingRequested, pairing.PairingId);

        return await InfoAsync(pairing, now, cancellationToken);
    }

    /// <summary>Um pareamento, só para as duas pontas dele. Para as outras, 404.</summary>
    public async Task<PairingInfo> GetAsync(SyncCaller caller, Guid pairingId, CancellationToken cancellationToken)
    {
        var pairing = await FindAsync(caller, pairingId, cancellationToken);

        if (pairing.ControllerDeviceId != caller.DeviceId && pairing.ExecutorDeviceId != caller.DeviceId)
        {
            throw SyncServerException.NotFound();
        }

        return await InfoAsync(pairing, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>Os pedidos em aberto para a máquina do token, os mais novos primeiro.</summary>
    public async Task<IReadOnlyList<PairingInfo>> ListOpenAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var open = await pairings.ListOpenForExecutorAsync(caller.UserId, caller.DeviceId, now, cancellationToken);
        var result = new List<PairingInfo>(open.Count);

        foreach (var pairing in open.OrderByDescending(pairing => pairing.CreatedAt))
        {
            result.Add(await InfoAsync(pairing, now, cancellationToken));
        }

        return result;
    }

    /// <summary>
    /// O executor aprova, assinando <see cref="PairingCode.ApprovalPayload"/>.
    /// As duas chaves precisam ser ainda as do pedido; aprovar de novo devolve o
    /// mesmo pareamento.
    /// </summary>
    public async Task<PairingInfo> ApproveAsync(SyncCaller caller, Guid pairingId, PairingApproval approval, CancellationToken cancellationToken)
    {
        var pairing = await FindAsync(caller, pairingId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (pairing.ExecutorDeviceId != caller.DeviceId)
        {
            throw SyncServerException.NotFound();
        }

        if (pairing.ApprovedAt is not null)
        {
            return await InfoAsync(pairing, now, cancellationToken);
        }

        if (pairing.ExpiresAt <= now)
        {
            throw new SyncServerException(RemoteErrorCodes.PairingExpired, 410, "O pedido de pareamento expirou. Peça de novo no controlador.");
        }

        var executorKey = await remote.FindActiveKeyAsync(caller.UserId, caller.DeviceId, cancellationToken);
        var controllerKey = await remote.FindActiveKeyAsync(caller.UserId, pairing.ControllerDeviceId, cancellationToken);

        if (executorKey is null
            || controllerKey is null
            || executorKey.Fingerprint != pairing.ExecutorKeyFingerprint
            || controllerKey.Fingerprint != pairing.ControllerKey.Fingerprint)
        {
            throw new SyncServerException(RemoteErrorCodes.KeyChanged, 409, "Uma das chaves mudou depois do pedido. Peça o pareamento de novo.");
        }

        var payload = PairingCode.ApprovalPayload(pairing.PairingId, pairing.ControllerKey.Fingerprint, pairing.ExecutorKeyFingerprint, pairing.Nonce);

        if (!executorKey.Verifies(payload, approval.Signature))
        {
            throw new SyncServerException(RemoteErrorCodes.UntrustedSignature, 403, "A assinatura não é desta máquina.");
        }

        await pairings.ApproveAsync(caller.UserId, pairing.PairingId, now, cancellationToken);
        pairing.ApprovedAt = now;

        logger.LogInformation(
            "RemotePairingApproved {UserId} {PairingId} {ControllerDeviceId} {ExecutorDeviceId}",
            caller.UserId, pairing.PairingId, pairing.ControllerDeviceId, pairing.ExecutorDeviceId);

        await notifier.ToAccountAsync(caller.UserId, RemoteHubMessages.PairingApproved, pairing.PairingId);

        return await InfoAsync(pairing, now, cancellationToken);
    }

    private async Task<PairingRecord> FindAsync(SyncCaller caller, Guid pairingId, CancellationToken cancellationToken) =>
        await pairings.FindAsync(caller.UserId, pairingId, cancellationToken) ?? throw SyncServerException.NotFound();

    private async Task<PairingInfo> InfoAsync(PairingRecord pairing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controller = await devices.FindAsync(pairing.UserId, pairing.ControllerDeviceId, cancellationToken);

        return new PairingInfo(
            pairing.PairingId,
            pairing.ControllerDeviceId,
            controller?.Nickname ?? DeviceUseCases.DefaultNickname,
            pairing.ControllerKey,
            pairing.ExecutorDeviceId,
            pairing.ExecutorKeyFingerprint,
            pairing.Nonce,
            pairing.ApprovedAt is not null ? PairingStatus.Approved
                : pairing.ExpiresAt <= now ? PairingStatus.Expired
                : PairingStatus.Pending,
            pairing.ExpiresAt);
    }
}

/// <summary>Um pedido de pareamento guardado.</summary>
public sealed class PairingRecord
{
    public required Guid PairingId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid ControllerDeviceId { get; init; }

    /// <summary>A chave do controlador quando ele pediu: é ela que o executor passa a confiar.</summary>
    public required DevicePublicKey ControllerKey { get; init; }

    public required Guid ExecutorDeviceId { get; init; }

    public required string ExecutorKeyFingerprint { get; init; }

    public required string Nonce { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; set; }
}

public interface IRemotePairingStore
{
    Task AddAsync(PairingRecord pairing, CancellationToken cancellationToken);

    Task<PairingRecord?> FindAsync(Guid userId, Guid pairingId, CancellationToken cancellationToken);

    /// <summary>Os não aprovados e não expirados da conta.</summary>
    Task<int> CountOpenAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<PairingRecord>> ListOpenForExecutorAsync(Guid userId, Guid executorDeviceId, DateTimeOffset now, CancellationToken cancellationToken);

    Task ApproveAsync(Guid userId, Guid pairingId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Os vencidos antes de <paramref name="expiredBefore"/>, aprovados ou não; quantos saíram.</summary>
    Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken cancellationToken);
}
