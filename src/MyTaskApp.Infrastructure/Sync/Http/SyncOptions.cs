using System.Reflection;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// O servidor de sincronização e os limites das chamadas (ADR-060).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhum segredo.</b> O desktop é cliente público do OAuth: sem client
/// secret, com PKCE. O que o build grava é só o endereço do servidor
/// (<c>-p:SyncApiBaseUrl=…</c>), e a seção <c>Sync</c> da configuração o
/// sobrepõe — é assim que o desenvolvimento aponta para o servidor local.
/// </para>
/// <para>
/// Sem endereço, a sincronização não aparece como opção: o app continua
/// inteiro, só local.
/// </para>
/// </remarks>
internal sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public const string ApiBaseUrlMetadata = "SyncApiBaseUrl";

    public const string SecretName = "sync";

    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// A porta da volta do login, em <c>http://127.0.0.1:{porta}/callback</c>.
    /// Fixa: o servidor só aceita o endereço exato registrado.
    /// </summary>
    public int CallbackPort { get; set; } = 47833;

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int AuthorizationTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// O endereço do servidor, se configurado e seguro: HTTPS, ou HTTP só no
    /// loopback (o servidor de desenvolvimento). Um HTTP de rede mandaria o
    /// token em claro, e é recusado.
    /// </summary>
    public Uri? BaseUri =>
        Uri.TryCreate(ApiBaseUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/")
            : null;

    public bool IsAvailable => BaseUri is not null;

    public Uri RedirectUri => new($"http://127.0.0.1:{CallbackPort}/callback");

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 5, 120));

    public TimeSpan AuthorizationTimeout => TimeSpan.FromMinutes(Math.Clamp(AuthorizationTimeoutMinutes, 1, 30));

    public static SyncOptions FromBuild() => new()
    {
        ApiBaseUrl = typeof(SyncOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == ApiBaseUrlMetadata)?.Value,
    };
}
