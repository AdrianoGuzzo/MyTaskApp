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

    /// <summary>Os registros que esta máquina mandou, em ordem.</summary>
    public List<DeviceRegistration> Registrations { get; } = [];

    /// <summary>As máquinas da conta, como o servidor as devolve.</summary>
    public List<RemoteDeviceInfo> Devices { get; } = [];

    /// <summary>A recusa que a próxima chamada leva, como a do servidor (sem escopo, revogado, fora do ar).</summary>
    public Exception? Failure { get; set; }

    public Task<RemoteDeviceInfo> RegisterAsync(DeviceRegistration registration, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Registrations.Add(registration);

        var current = new RemoteDeviceInfo(
            registration.DeviceId, "Este", "windows", "1.0.0", true, registration.Roles, registration.Key.Fingerprint, registration.Capabilities, DateTimeOffset.UnixEpoch);
        Devices.RemoveAll(device => device.DeviceId == registration.DeviceId);
        Devices.Insert(0, current);
        return Task.FromResult(current);
    }

    public Task<IReadOnlyList<RemoteDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<RemoteDeviceInfo>>([.. Devices]);
    }

    public Task<PairingInfo> RequestPairingAsync(Guid executorDeviceId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var pairing = new PairingInfo(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Este", DevicePublicKey.Es256Of([1]), executorDeviceId,
            Devices.Single(device => device.DeviceId == executorDeviceId).KeyFingerprint!, "bm9uY2U", PairingStatus.Pending, DateTimeOffset.UnixEpoch.AddMinutes(10));
        Pairings[pairing.PairingId] = pairing;
        return Task.FromResult(pairing);
    }

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

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

internal sealed class FakeTrustedControllerRepository : ITrustedControllerRepository, IUnitOfWork
{
    public List<TrustedController> All { get; } = [];

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<TrustedController>> ListActiveAsync(Guid controllerDeviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrustedController>>([.. All.Where(controller => controller.ControllerDeviceId == controllerDeviceId && controller.IsActive)]);

    public Task<IReadOnlyList<TrustedController>> ListAllActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrustedController>>([.. All.Where(controller => controller.IsActive)]);

    public Task<TrustedController?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(All.SingleOrDefault(controller => controller.Id == id));

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

/// <summary>O remoto desta instalação, na memória. Padrão: ligado e aceitando controle.</summary>
internal sealed class FakeRemoteSettingsStore(RemoteSettings? settings = null) : IRemoteSettingsStore
{
    public RemoteSettings Settings { get; set; } = settings ?? new RemoteSettings(true, true);

    public Task<RemoteSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

    public Task SaveAsync(RemoteSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
