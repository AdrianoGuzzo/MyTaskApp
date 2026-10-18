namespace MyTaskApp.Sync.Contracts;

/// <summary>
/// A versão do contrato. Sobe quando entra um tipo de entidade ou um campo que
/// um cliente antigo não saberia guardar; o servidor recusa (426) quem estiver
/// abaixo de <see cref="MinimumClient"/>.
/// </summary>
/// <remarks>
/// Campo novo cabe na mesma versão, desde que o <b>servidor seja atualizado
/// antes</b> (ADR-072): ele recusa o campo que não conhece (<c>unknown_field</c>),
/// e o cliente mais velho ignora o que não conhece no pull.
/// </remarks>
public static class SyncSchema
{
    public const int Current = 1;

    public const int MinimumClient = 1;

    /// <summary>O escopo OAuth que dá acesso à API de sincronização.</summary>
    public const string Scope = "sync";

    /// <summary>O cliente OAuth público do desktop (sem secret; PKCE obrigatório).</summary>
    public const string DesktopClientId = "mytaskapp-desktop";

    /// <summary>
    /// O cliente OAuth público do celular (ADR-067): também sem secret e com
    /// PKCE, mas a volta do login é um esquema próprio do app, não o loopback.
    /// </summary>
    public const string MobileClientId = "mytaskapp-mobile";

    /// <summary>
    /// O esquema da volta do login no celular. Em domínio reverso (RFC 8252),
    /// para não colidir com o de outro app instalado.
    /// </summary>
    public const string MobileRedirectScheme = "io.github.adrianoguzzo.mytaskapp";

    /// <summary>O endereço exato de volta do celular; o servidor compara byte a byte.</summary>
    public const string MobileRedirectUri = MobileRedirectScheme + "://oauth/callback";

    /// <summary>A claim e o parâmetro de autorização que carregam o dispositivo.</summary>
    public const string DeviceIdClaim = "device_id";

    /// <summary>O apelido sugerido pelo desktop na autorização (o nome da máquina).</summary>
    public const string DeviceNameParameter = "device_name";

    public const int MaxNicknameLength = 60;

    /// <summary>O teto das capacidades do "olá" (SEC-015): quantas e o tamanho de cada uma.</summary>
    public const int MaxCapabilities = 16;

    public const int MaxCapabilityLength = 64;

    public const int MaxOperationsPerPush = 100;

    public const int DefaultPullLimit = 200;

    public const int MaxPullLimit = 500;

    public const int MaxFetchEntities = 100;
}

/// <summary>As rotas da API v1, relativas à raiz do servidor.</summary>
public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    public const string Me = Prefix + "/me";

    public const string Workspaces = Prefix + "/workspaces";

    public const string Devices = Prefix + "/devices";

    public const string CurrentDevice = Devices + "/current";

    public static string Device(Guid deviceId) => $"{Devices}/{deviceId:D}";

    public static string RevokeDevice(Guid deviceId) => $"{Device(deviceId)}/revoke";

    public static string Push(Guid workspaceId) => $"{Workspaces}/{workspaceId:D}/sync/push";

    public static string Pull(Guid workspaceId) => $"{Workspaces}/{workspaceId:D}/sync/pull";

    public static string Fetch(Guid workspaceId) => $"{Workspaces}/{workspaceId:D}/sync/fetch";

    public static string Status(Guid workspaceId) => $"{Workspaces}/{workspaceId:D}/sync/status";

    public const string Authorize = "/connect/authorize";

    public const string Token = "/connect/token";

    public const string Revocation = "/connect/revoke";
}
