using System.Buffers.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// A chave do dispositivo para quem compõe sem o <c>AddInfrastructure</c>: o
/// celular (T-34) a embrulha para só assinar depois de destravar.
/// </summary>
public static class DeviceSigning
{
    public static IDeviceSigner Create(ISecretStore secrets, ILoggerFactory loggers) =>
        new DeviceSigner(new DeviceKeyStore(secrets, loggers.CreateLogger<DeviceKeyStore>()));
}

/// <summary>A porta da chave do dispositivo (ADR-087) sobre o <see cref="DeviceKeyStore"/>.</summary>
internal sealed class DeviceSigner(DeviceKeyStore keys) : IDeviceSigner
{
    public async Task<DevicePublicKey> GetPublicKeyAsync(CancellationToken cancellationToken)
    {
        using var key = await keys.GetOrCreateAsync(cancellationToken);
        return key.PublicKey;
    }

    public async Task<string> SignAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var key = await keys.GetOrCreateAsync(cancellationToken);
        return Base64Url.EncodeToString(key.Sign(data));
    }
}
