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
