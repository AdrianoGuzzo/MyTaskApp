using System.Buffers.Text;
using System.Security.Cryptography;

namespace MyTaskApp.Remote.Contracts;

/// <summary>O que um dispositivo faz na orquestração. Uma instalação pode ser os dois.</summary>
[Flags]
public enum DeviceRoles
{
    None = 0,
    Controller = 1,

    /// <summary>Só quando "Aceitar controle remoto" foi ligado na própria máquina (ADR-087).</summary>
    Executor = 2,
}

/// <summary>
/// O dispositivo do token se apresenta à orquestração. O <paramref name="DeviceId"/>
/// é o mesmo do sync: uma instalação, uma identidade.
/// </summary>
public sealed record DeviceRegistration(
    Guid DeviceId,
    DeviceRoles Roles,
    DevicePublicKey Key,
    DeviceCapabilities Capabilities);

/// <summary>A chave pública com que o dispositivo assina. A privada nunca sai dele.</summary>
/// <param name="Algorithm">Sempre <see cref="Es256"/> na v1.</param>
/// <param name="SubjectPublicKeyInfo">O SPKI em base64url.</param>
/// <param name="Fingerprint">SHA-256 do SPKI, em hexadecimal; a tela mostra o começo.</param>
public sealed record DevicePublicKey(string Algorithm, string SubjectPublicKeyInfo, string Fingerprint)
{
    /// <summary>ECDSA P-256 com SHA-256: há no .NET do Windows, do Linux e do Android.</summary>
    public const string Es256 = "ES256";

    /// <summary>A chave ES256 de um SPKI, com a impressão digital calculada dele.</summary>
    public static DevicePublicKey Es256Of(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        new(Es256, Base64Url.EncodeToString(subjectPublicKeyInfo), FingerprintOf(subjectPublicKeyInfo));

    /// <summary>
    /// SHA-256 do SPKI, em hexadecimal minúsculo. O agente, o servidor e o
    /// celular calculam por aqui, para o código de pareamento bater nos três.
    /// </summary>
    public static string FingerprintOf(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    /// <summary>
    /// ES256 de verdade: o SPKI em base64url abre como uma chave pública P-256,
    /// e a impressão digital é a dele. O servidor confere ao registrar, e o
    /// agente ao aceitar a chave de um controlador.
    /// </summary>
    public bool IsWellFormedEs256()
    {
        if (Algorithm != Es256
            || SubjectPublicKeyInfo.Length > RemoteLimits.MaxSubjectPublicKeyInfoLength
            || !Base64Url.IsValid(SubjectPublicKeyInfo, out _))
        {
            return false;
        }

        var spki = Base64Url.DecodeFromChars(SubjectPublicKeyInfo);

        if (Base64Url.EncodeToString(spki) != SubjectPublicKeyInfo || Fingerprint != FingerprintOf(spki))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);

            return read == spki.Length
                && key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>O que o agente sabe fazer, e com quais ferramentas desta máquina.</summary>
/// <param name="Operations">Os tipos de <see cref="RemoteOperationKinds"/> que esta versão do agente atende.</param>
/// <param name="CatalogHash">Muda quando o <see cref="AgentCatalog"/> muda: o controlador sabe quando reler.</param>
public sealed record DeviceCapabilities(
    int ProtocolVersion,
    IReadOnlyList<string> Operations,
    string OperatingSystem,
    ToolStatus Git,
    ToolStatus ClaudeCode,
    ToolStatus GitHubCli,
    ToolStatus PostgresTools,
    int MaxConcurrentOperations,
    string CatalogHash);

public sealed record ToolStatus(bool Available, string? Version)
{
    public static readonly ToolStatus Missing = new(false, null);
}

/// <summary>
/// Um dispositivo da conta, como a lista "Máquinas" mostra
/// (<c>GET /api/v1/remote/devices</c>). Quem nunca se registrou no remoto
/// aparece sem papéis, sem chave e sem capacidades.
/// </summary>
/// <param name="IsCurrent">É o dispositivo de quem pergunta.</param>
/// <param name="LastHeartbeatAt">O último sinal de vida na orquestração; <c>null</c> se nunca houve.</param>
public sealed record RemoteDeviceInfo(
    Guid DeviceId,
    string Nickname,
    string Platform,
    string AppVersion,
    bool IsCurrent,
    DeviceRoles Roles,
    string? KeyFingerprint,
    DeviceCapabilities? Capabilities,
    DateTimeOffset? LastHeartbeatAt)
{
    /// <summary>Deu sinal de vida há no máximo <see cref="RemoteLimits.OfflineAfter"/>.</summary>
    public bool IsOnlineAt(DateTimeOffset now) =>
        LastHeartbeatAt is { } at && now - at <= RemoteLimits.OfflineAfter;
}
