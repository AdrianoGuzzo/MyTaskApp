using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyTaskApp.Application.External.Jira;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>Os tokens de uma troca ou renovação. Só vivem na memória e no cofre.</summary>
internal sealed record JiraTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// O OAuth 2.0 (3LO) da Atlassian (ADR-045): o endereço de autorização, a
/// troca do código, a renovação e a lista de sites que a conta autorizou.
/// </summary>
/// <remarks>
/// O PKCE (S256) vai junto mesmo com o secret: a Atlassian aceita, e ele faz
/// um código interceptado na volta pelo <c>localhost</c> não servir para mais
/// ninguém. O <c>offline_access</c> é o que traz o refresh token — sem ele, o
/// usuário reconectaria a cada hora.
/// </remarks>
internal sealed class JiraOAuthClient(JiraHttp http, JiraOptions options, TimeProvider timeProvider)
{
    public const string Scopes = "read:jira-work read:jira-user offline_access";

    public static readonly Uri AuthorizeEndpoint = new("https://auth.atlassian.com/authorize");

    public static readonly Uri TokenEndpoint = new("https://auth.atlassian.com/oauth/token");

    public static readonly Uri ResourcesEndpoint = new("https://api.atlassian.com/oauth/token/accessible-resources");

    public static readonly Uri ApiGateway = new("https://api.atlassian.com/ex/jira/");

    /// <summary>A margem para não usar um token que vence no meio da chamada.</summary>
    private static readonly TimeSpan ExpirySafety = TimeSpan.FromMinutes(1);

    public Uri AuthorizeUrl(string state, string codeChallenge)
    {
        var query = new StringBuilder()
            .Append("audience=api.atlassian.com")
            .Append("&client_id=").Append(Uri.EscapeDataString(options.ClientId!))
            .Append("&scope=").Append(Uri.EscapeDataString(Scopes))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(options.RedirectUri.ToString()))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&response_type=code")
            .Append("&prompt=consent")
            .Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge))
            .Append("&code_challenge_method=S256");

        return new UriBuilder(AuthorizeEndpoint) { Query = query.ToString() }.Uri;
    }

    public Task<JiraTokens> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken) =>
        RequestTokensAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["code"] = code,
                ["redirect_uri"] = options.RedirectUri.ToString(),
                ["code_verifier"] = codeVerifier,
            },
            cancellationToken);

    /// <summary>
    /// A Atlassian gira o refresh token: o antigo deixa de valer quando o novo
    /// chega, então quem chama tem de gravar o que voltou.
    /// </summary>
    public Task<JiraTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        RequestTokensAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["refresh_token"] = refreshToken,
            },
            cancellationToken);

    /// <summary>Os sites do Jira que a autorização alcança. Confluence e companhia ficam de fora.</summary>
    public async Task<IReadOnlyList<JiraSite>> ListSitesAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var document = await http.GetAsync(ResourcesEndpoint, Bearer(accessToken), cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JiraHttpException(JiraHttpFailure.Server);
        }

        return document.RootElement
            .EnumerateArray()
            .Where(site => site.TryGetProperty("scopes", out var scopes)
                && scopes.EnumerateArray().Any(scope => scope.GetString()?.Contains("jira", StringComparison.OrdinalIgnoreCase) == true))
            .Select(site => new JiraSite(
                site.GetProperty("id").GetString()!,
                site.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                site.GetProperty("url").GetString()!.TrimEnd('/')))
            .ToList();
    }

    public static AuthenticationHeaderValue Bearer(string accessToken) => new("Bearer", accessToken);

    /// <summary>A API de um site, pelo gateway da Atlassian: <c>…/ex/jira/{cloudId}/</c>.</summary>
    public static Uri ApiBaseFor(string cloudId) => new(ApiGateway, Uri.EscapeDataString(cloudId) + "/");

    /// <summary>Um <c>state</c> ou <c>code_verifier</c>: 32 bytes aleatórios em base64url.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<JiraTokens> RequestTokensAsync(Dictionary<string, string> body, CancellationToken cancellationToken)
    {
        using var document = await http.PostJsonAsync(TokenEndpoint, body, authorization: null, cancellationToken);
        var root = document.RootElement;

        var expiresIn = root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600;

        return new JiraTokens(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            timeProvider.GetUtcNow() + TimeSpan.FromSeconds(expiresIn) - ExpirySafety);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
