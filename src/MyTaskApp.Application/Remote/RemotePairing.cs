using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>A orquestração no servidor, vista desta máquina (ADR-084).</summary>
public interface IRemoteApi
{
    Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken);

    /// <summary>Os pedidos de pareamento em aberto para esta máquina.</summary>
    Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken);

    Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken);
}

/// <summary>A chave desta instalação (ADR-087): a pública para mostrar e registrar, e a assinatura.</summary>
public interface IDeviceSigner
{
    Task<DevicePublicKey> GetPublicKeyAsync(CancellationToken cancellationToken);

    /// <summary>ES256, r e s em base64url, como o servidor e o agente conferem.</summary>
    Task<string> SignAsync(byte[] data, CancellationToken cancellationToken);
}

/// <summary>Os controladores em que esta máquina confia. Estreita por necessidade (ADR-005).</summary>
public interface ITrustedControllerRepository
{
    /// <summary>Os ainda valendo do dispositivo controlador.</summary>
    Task<IReadOnlyList<TrustedController>> ListActiveAsync(Guid controllerDeviceId, CancellationToken cancellationToken = default);

    Task AddAsync(TrustedController controller, CancellationToken cancellationToken = default);
}

/// <summary>O que a tela da máquina executora mostra de um pedido: quem pede e até quando. O código, não.</summary>
public sealed record OpenPairingView(Guid PairingId, Guid ControllerDeviceId, string ControllerNickname, DateTimeOffset ExpiresAt);

public sealed record GetOpenPairings;

public sealed class GetOpenPairingsHandler(IRemoteApi remote)
{
    public async Task<IReadOnlyList<OpenPairingView>> HandleAsync(GetOpenPairings query, CancellationToken cancellationToken = default) =>
        [
            .. (await remote.ListOpenPairingsAsync(cancellationToken))
                .Where(pairing => pairing.Status == PairingStatus.Pending)
                .Select(pairing => new OpenPairingView(pairing.PairingId, pairing.ControllerDeviceId, pairing.ControllerNickname, pairing.ExpiresAt)),
        ];
}

/// <summary>A pessoa digita, na máquina executora, o código que o controlador mostra.</summary>
public sealed record ConfirmPairing(Guid PairingId, string TypedCode);

/// <summary>
/// Aprova um pareamento nesta máquina (fase 2, T-05; RS-08). O código é
/// calculado aqui, da chave do controlador que veio no pedido e da chave
/// desta máquina, e precisa bater com o que a pessoa leu no controlador. Se
/// o servidor tiver trocado uma das chaves, não bate, e nada é aprovado nem
/// gravado. A tela não mostra o código: digitar o que o controlador mostra é
/// o que prova que as duas pontas viram as mesmas chaves.
/// </summary>
/// <remarks>
/// O servidor aprova primeiro e a confiança local é gravada depois: se a
/// gravação falhar, o controlador acha que pareou, e a pessoa pareia de novo.
/// O contrário deixaria esta máquina confiando num pedido que o servidor
/// ainda trata como aberto.
/// </remarks>
public sealed class ConfirmPairingHandler(
    IRemoteApi remote,
    IDeviceSigner signer,
    IDeviceIdentity identity,
    ITrustedControllerRepository controllers,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ConfirmPairingHandler> logger)
{
    public async Task<TrustedController> HandleAsync(ConfirmPairing command, CancellationToken cancellationToken = default)
    {
        var pairing = await remote.GetPairingAsync(command.PairingId, cancellationToken);

        if (pairing.Status != PairingStatus.Pending)
        {
            throw new DomainException("Este pedido de pareamento não está mais aberto. Peça de novo no controlador.");
        }

        if (pairing.ExecutorDeviceId != identity.DeviceId)
        {
            throw new DomainException("Este pedido de pareamento é para outra máquina.");
        }

        var key = await signer.GetPublicKeyAsync(cancellationToken);

        if (pairing.ExecutorKeyFingerprint != key.Fingerprint || !pairing.ControllerKey.IsWellFormedEs256())
        {
            throw new DomainException("As chaves do pedido não são as desta máquina e do controlador. Peça o pareamento de novo.");
        }

        var expected = PairingCode.Derive(pairing.ControllerKey.Fingerprint, key.Fingerprint, pairing.Nonce);

        if (!PairingCode.Matches(expected, command.TypedCode))
        {
            logger.LogWarning("RemotePairingCodeMismatch {PairingId} {ControllerDeviceId}", pairing.PairingId, pairing.ControllerDeviceId);
            throw new DomainException("O código não confere com o do controlador. Nada foi aprovado.");
        }

        var signature = await signer.SignAsync(
            PairingCode.ApprovalPayload(pairing.PairingId, pairing.ControllerKey.Fingerprint, key.Fingerprint, pairing.Nonce),
            cancellationToken);

        await remote.ApprovePairingAsync(pairing.PairingId, new PairingApproval(signature), cancellationToken);

        var now = timeProvider.GetUtcNow();

        // Uma chave por controlador: parear de novo o mesmo dispositivo tira a confiança na anterior.
        foreach (var previous in await controllers.ListActiveAsync(pairing.ControllerDeviceId, cancellationToken))
        {
            previous.Revoke(now);
        }

        var trusted = TrustedController.Approve(
            pairing.ControllerDeviceId,
            pairing.ControllerNickname,
            pairing.ControllerKey.Fingerprint,
            pairing.ControllerKey.SubjectPublicKeyInfo,
            pairing.PairingId,
            now);

        await controllers.AddAsync(trusted, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("RemotePairingConfirmed {PairingId} {ControllerDeviceId}", pairing.PairingId, pairing.ControllerDeviceId);

        return trusted;
    }
}
