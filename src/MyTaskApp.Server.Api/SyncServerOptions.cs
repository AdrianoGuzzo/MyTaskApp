using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Api;

/// <summary>
/// A configuração do servidor (ADR-061). Os segredos — client secret do
/// Google, certificados do OpenIddict, senha do banco — vêm de arquivos em
/// <c>/run/secrets</c>, de variáveis de ambiente ou do user-secrets, nunca do
/// repositório.
/// </summary>
public sealed class SyncServerOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// O endereço público do servidor, como <c>https://sync.exemplo.com/</c>: o
    /// issuer do discovery e dos tokens (SEC-007). Obrigatório fora de
    /// Development. Vazio, quem decide é o <c>Host</c> da requisição.
    /// </summary>
    public Uri? Issuer { get; set; }

    /// <summary>
    /// Os endereços de volta aceitos para o desktop. O loopback vai sem porta:
    /// o cliente é nativo, e o OpenIddict aceita nele a porta efêmera que o
    /// desktop abriu (RFC 8252 §7.3, ADR-074). O resto é comparado exatamente.
    /// </summary>
    public string[] DesktopRedirectUris { get; set; } = ["http://127.0.0.1/callback"];

    /// <summary>
    /// Os endereços de volta aceitos para o celular (ADR-067): o esquema
    /// próprio do app. Vazio = o servidor não aceita login do celular.
    /// </summary>
    public string[] MobileRedirectUris { get; set; } = [SyncSchema.MobileRedirectUri];

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Quanto tempo o refresh token já trocado ainda é aceito: cobre a resposta
    /// da renovação que se perdeu na rede. Depois disso, reusar revoga a cadeia.
    /// </summary>
    public TimeSpan RefreshTokenReuseLeeway { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Por quanto tempo "este dispositivo não foi revogado" fica em memória.</summary>
    public TimeSpan DeviceCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quantos dispositivos ativos (não revogados) uma conta pode ter (SEC-015).
    /// O seguinte é recusado no authorize com <c>device_limit_reached</c>. Zero desliga.
    /// </summary>
    public int MaxActiveDevicesPerUser { get; set; } = 20;

    /// <summary>Por quanto tempo o resultado de uma operação fica guardado para o reenvio.</summary>
    public TimeSpan ProcessedOperationRetention { get; set; } = TimeSpan.FromDays(90);

    public RateLimitOptions RateLimits { get; set; } = new();

    public RegistrationOptions Registration { get; set; } = new();

    /// <summary>Os tetos de entidades e de bytes por página do pull (ADR-071).</summary>
    public PullLimits Pull { get; set; } = new();

    /// <summary>O teto de bytes por entidade e a cota do workspace no push (ADR-072).</summary>
    public PushLimits Push { get; set; } = new();

    public CertificateOptions? SigningCertificate { get; set; }

    public CertificateOptions? EncryptionCertificate { get; set; }

    /// <summary>
    /// O PFX que cifra as chaves do Data Protection no banco (SEC-011,
    /// ADR-073). Obrigatório fora de Development; sem ele, quem lê a tabela
    /// forja o cookie do login.
    /// </summary>
    public CertificateOptions? DataProtectionCertificate { get; set; }

    /// <summary>Os PFX de antes da rotação: só decifram as chaves que eles cifraram.</summary>
    public CertificateOptions[] DataProtectionPreviousCertificates { get; set; } = [];
}

/// <summary>Os limites por janela de um minuto (ADR-061, ADR-069).</summary>
public sealed class RateLimitOptions
{
    /// <summary>Login e emissão de token, por IP.</summary>
    public int AuthPerMinute { get; set; } = 20;

    /// <summary>Push, pull e fetch, pelo <c>sub</c> do token.</summary>
    public int SyncPerMinute { get; set; } = 60;

    public int SyncBurst { get; set; } = 20;

    /// <summary>O resto da API, pelo <c>sub</c> do token.</summary>
    public int ApiPerMinute { get; set; } = 120;

    /// <summary>A orquestração remota, pelo <c>sub</c> do token, num balde separado do sync.</summary>
    public int RemotePerMinute { get; set; } = 120;

    /// <summary>Rotas da API sem token válido, por IP: não gastam a cota de ninguém.</summary>
    public int AnonymousPerMinute { get; set; } = 30;

    /// <summary>Teto de proteção de qualquer rota, por IP, somado aos outros. Zero desliga.</summary>
    public int GlobalPerMinute { get; set; } = 600;
}

/// <summary>
/// Quem pode usar o servidor (ADR-068): a conta Google precisa estar numa das
/// listas. As duas vazias = ninguém entra; o servidor nunca abre por esquecimento.
/// </summary>
public sealed class RegistrationOptions
{
    /// <summary>E-mails aceitos, sem diferença de maiúsculas. Só vale o e-mail que o Google diz verificado.</summary>
    public string[] AllowedEmails { get; set; } = [];

    /// <summary>O <c>sub</c> do Google: o id que não muda, nem quando o e-mail da conta muda.</summary>
    public string[] AllowedSubjects { get; set; } = [];

    public bool Allows(IEnumerable<string> subjects, string? email, bool emailVerified) =>
        subjects.Any(subject => AllowedSubjects.Any(allowed => string.Equals(allowed.Trim(), subject, StringComparison.Ordinal)))
        || (emailVerified
            && !string.IsNullOrWhiteSpace(email)
            && AllowedEmails.Any(allowed => string.Equals(allowed.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Um certificado PFX: por arquivo, ou em base64 numa variável de ambiente.</summary>
public sealed class CertificateOptions
{
    public string? Path { get; set; }

    public string? Base64 { get; set; }

    public string? Password { get; set; }
}

/// <summary>O cliente OAuth do Google. Sem ele, o servidor sobe, mas ninguém entra.</summary>
public sealed class GoogleOptions
{
    public const string SectionName = "Authentication:Google";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
