using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MyTaskApp.Remote.Contracts;

/// <summary>Onde um pareamento está. Expirado é calculado: passou de <c>ExpiresAt</c> sem aprovação.</summary>
public enum PairingStatus
{
    Pending = 1,
    Approved = 2,
    Expired = 3,
}

/// <summary><c>POST /api/v1/remote/pairings</c>: o controlador do token pede para controlar a máquina.</summary>
public sealed record PairingRequest(Guid ExecutorDeviceId);

/// <summary>
/// Um pedido de pareamento (ADR-087, RS-08). A chave do controlador e a
/// impressão digital do executor são as que valiam quando ele foi criado; o
/// código sai delas e do <paramref name="Nonce"/>, calculado em cada ponta.
/// </summary>
/// <param name="Nonce">16 bytes aleatórios em base64url, sorteados pelo servidor.</param>
public sealed record PairingInfo(
    Guid PairingId,
    Guid ControllerDeviceId,
    string ControllerNickname,
    DevicePublicKey ControllerKey,
    Guid ExecutorDeviceId,
    string ExecutorKeyFingerprint,
    string Nonce,
    PairingStatus Status,
    DateTimeOffset ExpiresAt);

/// <summary>
/// <c>POST /api/v1/remote/pairings/{id}/approve</c>: o executor assina
/// <see cref="PairingCode.ApprovalPayload"/> com a chave dele, ES256 em base64url.
/// </summary>
public sealed record PairingApproval(string Signature);

/// <summary>
/// O código de comparação do pareamento: os dois lados calculam das duas
/// impressões digitais e do nonce, e a pessoa confere que é o mesmo. Um
/// servidor que troque uma das chaves no meio faz os códigos diferirem.
/// </summary>
public static class PairingCode
{
    public const int Digits = 6;

    private const string Domain = "mytaskapp-pairing-v1";

    public static string Derive(string controllerFingerprint, string executorFingerprint, string nonce)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Domain, controllerFingerprint, executorFingerprint, nonce)));
        var number = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;

        return number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// O que o executor assina para aprovar: o pareamento e as duas chaves
    /// exatas. A assinatura não serve para outro pareamento nem outra chave.
    /// </summary>
    public static byte[] ApprovalPayload(Guid pairingId, string controllerFingerprint, string executorFingerprint, string nonce) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Domain + "-approve", pairingId.ToString("D"), controllerFingerprint, executorFingerprint, nonce));

    /// <summary>O código como a pessoa digita: espaços e hífens não contam ("482 913", "482-913").</summary>
    public static bool Matches(string expected, string typed)
    {
        var digits = new string([.. typed.Where(character => character is not (' ' or '-'))]);
        return digits.Length == Digits && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digits), Encoding.ASCII.GetBytes(expected));
    }
}
