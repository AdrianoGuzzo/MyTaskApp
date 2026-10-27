using MyTaskApp.Application.Remote;
using MyTaskApp.Domain;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>
/// A chave do celular só assina depois de destravar (E7): quem pegou o
/// aparelho já desbloqueado não pede nem aprova nada em nome do dono. Um
/// destravamento vale por <see cref="Window"/>, para o pedido e a aprovação
/// logo em seguida não perguntarem duas vezes; ir para segundo plano tranca.
/// </summary>
/// <remarks>Mostrar a chave pública, para registrar e parear, não pede nada.</remarks>
public sealed class UnlockedDeviceSigner(IDeviceSigner inner, IDeviceUnlock unlock, TimeProvider timeProvider) : IDeviceSigner
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public const string Reason = "Confirme que é você para assinar o pedido.";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset? _unlockedAt;

    public Task<DevicePublicKey> GetPublicKeyAsync(CancellationToken cancellationToken) => inner.GetPublicKeyAsync(cancellationToken);

    public async Task<string> SignAsync(byte[] data, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_unlockedAt is not { } at || timeProvider.GetUtcNow() - at > Window)
            {
                switch (await unlock.UnlockAsync(Reason, cancellationToken))
                {
                    case UnlockResult.Unlocked:
                        _unlockedAt = timeProvider.GetUtcNow();
                        break;

                    case UnlockResult.NotAvailable:
                        throw new DomainException("Para pedir e aprovar de longe, o celular precisa de bloqueio de tela (PIN, padrão ou biometria).");

                    default:
                        throw new DomainException("Nada foi enviado: o celular não foi destravado.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return await inner.SignAsync(data, cancellationToken);
    }

    /// <summary>Foi para segundo plano: o próximo pedido destrava de novo.</summary>
    public void Lock() => _unlockedAt = null;
}
