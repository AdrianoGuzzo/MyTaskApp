namespace MyTaskApp.Domain.Remote;

/// <summary>
/// Um controlador em que esta máquina confia (ADR-087): a chave pública que a
/// pessoa aprovou aqui, conferindo o código do pareamento. O agente só executa
/// pedido assinado por uma chave desta lista.
/// </summary>
/// <remarks>
/// Não sincroniza (ADR-061) e nunca vem do servidor sem passar pela pessoa:
/// é a confiança desta máquina, e não da conta. Uma chave por controlador:
/// parear de novo o mesmo dispositivo revoga a anterior.
/// </remarks>
public sealed class TrustedController
{
    /// <summary>O mesmo teto do apelido no servidor (<c>SyncSchema.MaxNicknameLength</c>).</summary>
    public const int MaxNicknameLength = 60;

    /// <summary>SHA-256 em hexadecimal.</summary>
    public const int FingerprintLength = 64;

    public const int MaxSubjectPublicKeyInfoLength = 200;

    private TrustedController(
        Guid id,
        Guid controllerDeviceId,
        string nickname,
        string fingerprint,
        string subjectPublicKeyInfo,
        Guid pairingId,
        DateTimeOffset approvedAt)
    {
        Id = id;
        ControllerDeviceId = controllerDeviceId;
        Nickname = nickname;
        Fingerprint = fingerprint;
        SubjectPublicKeyInfo = subjectPublicKeyInfo;
        PairingId = pairingId;
        ApprovedAt = approvedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>O dispositivo do controlador, o mesmo id do sync.</summary>
    public Guid ControllerDeviceId { get; private set; }

    public string Nickname { get; private set; }

    public string Fingerprint { get; private set; }

    /// <summary>A chave pública ES256, SPKI em base64url, como foi aprovada.</summary>
    public string SubjectPublicKeyInfo { get; private set; }

    /// <summary>O pedido de pareamento que a pessoa confirmou.</summary>
    public Guid PairingId { get; private set; }

    public DateTimeOffset ApprovedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static TrustedController Approve(
        Guid controllerDeviceId,
        string nickname,
        string fingerprint,
        string subjectPublicKeyInfo,
        Guid pairingId,
        DateTimeOffset approvedAt)
    {
        if (controllerDeviceId == Guid.Empty || pairingId == Guid.Empty)
        {
            throw new DomainException("O pareamento precisa do controlador e do pedido.");
        }

        if (fingerprint.Length != FingerprintLength || !fingerprint.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainException("A impressão digital do controlador é inválida.");
        }

        if (subjectPublicKeyInfo.Length is 0 or > MaxSubjectPublicKeyInfoLength)
        {
            throw new DomainException("A chave do controlador é inválida.");
        }

        var name = nickname.Trim();

        return new TrustedController(
            Guid.CreateVersion7(),
            controllerDeviceId,
            name.Length is 0 ? "Controlador" : name.Length > MaxNicknameLength ? name[..MaxNicknameLength] : name,
            fingerprint,
            subjectPublicKeyInfo,
            pairingId,
            approvedAt);
    }

    /// <summary>Deixa de confiar. Revogar de novo não muda a hora.</summary>
    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}
