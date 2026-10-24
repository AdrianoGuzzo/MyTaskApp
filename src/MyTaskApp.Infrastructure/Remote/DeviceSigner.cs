using System.Buffers.Text;
using MyTaskApp.Application.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

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
