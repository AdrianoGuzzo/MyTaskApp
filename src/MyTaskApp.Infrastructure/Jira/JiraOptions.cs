using System.Reflection;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>
/// O app OAuth registrado na Atlassian e os limites das chamadas (ADR-045).
/// </summary>
/// <remarks>
/// <para>
/// <b>O client id e o secret não estão no repositório.</b> O OAuth 2.0 (3LO)
/// da Atlassian exige o secret na troca do código — não há cliente público nem
/// PKCE sem secret (pedido ECO-283). Eles entram no build pelo CI
/// (<c>-p:JiraClientId=… -p:JiraClientSecret=…</c>), viram metadado do
/// assembly e são lidos aqui. Quem compila sem eles ganha uma build sem o
/// botão de um clique, só com o caminho do API token.
/// </para>
/// <para>
/// A seção <c>Jira</c> da configuração sobrepõe o que veio do build. Em
/// desenvolvimento, é o <c>appsettings.user.json</c> que leva o app de teste.
/// </para>
/// <para>
/// O secret embarcado é extraível por quem tiver o executável — limite aceito
/// e registrado no ADR. Ele identifica o app, e não o usuário: sem o
/// consentimento no navegador, não abre conta de ninguém.
/// </para>
/// </remarks>
internal sealed class JiraOptions
{
    public const string SectionName = "Jira";

    public const string ClientIdMetadata = "JiraClientId";

    public const string ClientSecretMetadata = "JiraClientSecret";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// A porta da volta do OAuth em <c>http://localhost:{porta}/callback</c>.
    /// Fixa, porque a Atlassian só aceita o endereço exato que foi registrado.
    /// </summary>
    public int CallbackPort { get; set; } = 47832;

    /// <summary>Por chamada à API. Para o autocomplete, mais que isso já não parece busca.</summary>
    public int RequestTimeoutSeconds { get; set; } = 8;

    /// <summary>Quanto o app espera o usuário autorizar no navegador.</summary>
    public int AuthorizationTimeoutMinutes { get; set; } = 5;

    public bool IsOAuthAvailable =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public Uri RedirectUri => new($"http://localhost:{CallbackPort}/callback");

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 2, 60));

    public TimeSpan AuthorizationTimeout => TimeSpan.FromMinutes(Math.Clamp(AuthorizationTimeoutMinutes, 1, 30));

    /// <summary>O que o build gravou no assembly; vazio quando compilado sem o app OAuth.</summary>
    public static JiraOptions FromBuild()
    {
        var metadata = typeof(JiraOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);

        return new JiraOptions
        {
            ClientId = metadata.GetValueOrDefault(ClientIdMetadata),
            ClientSecret = metadata.GetValueOrDefault(ClientSecretMetadata),
        };
    }
}
