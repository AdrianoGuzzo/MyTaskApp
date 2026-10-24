using System.Buffers.Text;
using System.Security.Cryptography;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Uma chave ES256 de verdade, como a do cofre.</summary>
internal sealed class FakeDeviceSigner : IDeviceSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public DevicePublicKey PublicKey => DevicePublicKey.Es256Of(_key.ExportSubjectPublicKeyInfo());

    public int Signatures { get; private set; }

    public Task<DevicePublicKey> GetPublicKeyAsync(CancellationToken cancellationToken) => Task.FromResult(PublicKey);

    public Task<string> SignAsync(byte[] data, CancellationToken cancellationToken)
    {
        Signatures++;
        return Task.FromResult(Base64Url.EncodeToString(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// O servidor da orquestração em memória: guarda os pedidos e, na aprovação,
/// confere a assinatura com a chave do executor, como o de verdade.
/// </summary>
internal sealed class FakeRemoteApi : IRemoteApi
{
    public Dictionary<Guid, PairingInfo> Pairings { get; } = [];

    /// <summary>A chave registrada de cada executor.</summary>
    public Dictionary<Guid, DevicePublicKey> ExecutorKeys { get; } = [];

    public List<Guid> Approved { get; } = [];

    public Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken) =>
        Task.FromResult(Pairings[pairingId]);

    public Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PairingInfo>>([.. Pairings.Values]);

    public Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken)
    {
        var pairing = Pairings[pairingId];
        var payload = PairingCode.ApprovalPayload(pairing.PairingId, pairing.ControllerKey.Fingerprint, pairing.ExecutorKeyFingerprint, pairing.Nonce);

        if (!ExecutorKeys[pairing.ExecutorDeviceId].Verifies(payload, approval.Signature))
        {
            throw new InvalidOperationException("Assinatura que o servidor recusaria.");
        }

        Approved.Add(pairingId);
        Pairings[pairingId] = pairing with { Status = PairingStatus.Approved };
        return Task.FromResult(Pairings[pairingId]);
    }
}

internal sealed class FakeTrustedControllerRepository : ITrustedControllerRepository, IUnitOfWork
{
    public List<TrustedController> All { get; } = [];

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<TrustedController>> ListActiveAsync(Guid controllerDeviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrustedController>>([.. All.Where(controller => controller.ControllerDeviceId == controllerDeviceId && controller.IsActive)]);

    public Task AddAsync(TrustedController controller, CancellationToken cancellationToken = default)
    {
        All.Add(controller);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
