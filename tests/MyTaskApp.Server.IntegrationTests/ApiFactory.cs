using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Server.Api;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O servidor inteiro em memória (ADR-061), com o PostgreSQL de verdade por
/// baixo e um "Google" de mentira: o desafio dele já volta autenticado com o
/// usuário do cabeçalho <see cref="FakeGoogleHandler.SubjectHeader"/>.
/// </summary>
internal sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(ServerSetup.TestingEnvironment);
        builder.UseSetting("ConnectionStrings:Sync", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Sync:RefreshTokenReuseLeeway", "00:00:00");
        builder.UseSetting("Sync:RateLimits:AuthPerMinute", "1000");
        builder.UseSetting("Sync:RateLimits:SyncPerMinute", "1000");
        builder.UseSetting("Sync:RateLimits:SyncBurst", "1000");
        builder.UseSetting("Sync:RateLimits:ApiPerMinute", "1000");

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, FakeGoogleHandler>(AccountEndpoints.ExternalProvider, null);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    /// <summary>Um desktop: login pelo navegador, troca do código, e um cliente da API com o token.</summary>
    public async Task<DesktopSession> LoginAsync(string googleSubject, Guid? deviceId = null, string deviceName = "PC-TESTE")
    {
        var device = deviceId ?? Guid.CreateVersion7();
        var code = await AuthorizeAsync(googleSubject, device, deviceName);
        var tokens = await ExchangeAsync(code.Code, code.Verifier);

        return new DesktopSession(this, device, tokens);
    }

    /// <summary>Um celular (ADR-067): o mesmo login, pelo cliente e pelo esquema de volta do app.</summary>
    public async Task<DesktopSession> LoginMobileAsync(string googleSubject, Guid? deviceId = null, string deviceName = "Pixel 8")
    {
        var device = deviceId ?? Guid.CreateVersion7();
        var code = await AuthorizeAsync(googleSubject, device, deviceName, SyncSchema.MobileRedirectUri, clientId: SyncSchema.MobileClientId);
        var tokens = await ExchangeAsync(code.Code, code.Verifier, SyncSchema.MobileRedirectUri, SyncSchema.MobileClientId);

        return new DesktopSession(this, device, tokens);
    }

    public async Task<(string Code, string Verifier)> AuthorizeAsync(
        string googleSubject,
        Guid deviceId,
        string deviceName = "PC-TESTE",
        string redirectUri = DesktopSession.RedirectUri,
        bool withPkce = true,
        string clientId = SyncSchema.DesktopClientId)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access sync",
            ["state"] = state,
            [SyncSchema.DeviceIdClaim] = deviceId.ToString(),
            [SyncSchema.DeviceNameParameter] = deviceName,
        };

        if (withPkce)
        {
            query["code_challenge"] = challenge;
            query["code_challenge_method"] = "S256";
        }

        var callback = await FollowToCallbackAsync(googleSubject, "/connect/authorize?" + Encode(query));
        var parameters = Parse(callback.Query);

        parameters.GetValueOrDefault("state").Should().Be(state, "o state volta intacto");

        if (!parameters.TryGetValue("code", out var code))
        {
            throw new OAuthRefusedException(parameters.GetValueOrDefault("error"), parameters.GetValueOrDefault("error_description"));
        }

        return (code, verifier);
    }

    /// <summary>O navegador: segue os redirects até a volta para o loopback do desktop ou para o esquema do celular.</summary>
    public async Task<Uri> FollowToCallbackAsync(string googleSubject, string start)
    {
        using var browser = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(FakeGoogleHandler.SubjectHeader, googleSubject);

        var next = start;

        for (var hop = 0; hop < 10; hop++)
        {
            using var response = await browser.GetAsync(next);

            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther))
            {
                throw new OAuthRefusedException(response.StatusCode.ToString(), await response.Content.ReadAsStringAsync());
            }

            var location = response.Headers.Location!;

            if (location.IsAbsoluteUri && location.IsLoopback && location.AbsolutePath == "/callback")
            {
                return location;
            }

            // O esquema próprio do app: quem abre é o sistema do celular, não o servidor.
            if (location.IsAbsoluteUri && location.Scheme is not ("http" or "https"))
            {
                return location;
            }

            next = location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        }

        throw new InvalidOperationException("Redirects demais.");
    }

    public async Task<TokenSet> ExchangeAsync(
        string code,
        string? verifier,
        string redirectUri = DesktopSession.RedirectUri,
        string clientId = SyncSchema.DesktopClientId)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
        };

        if (verifier is not null)
        {
            form["code_verifier"] = verifier;
        }

        return await TokenRequestAsync(form);
    }

    public Task<TokenSet> RefreshAsync(string refreshToken, string clientId = SyncSchema.DesktopClientId) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        });

    public async Task RevokeAsync(string refreshToken)
    {
        using var client = CreateClient();
        using var response = await client.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = SyncSchema.DesktopClientId,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<TokenSet> TokenRequestAsync(Dictionary<string, string> form)
    {
        using var client = CreateClient();
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthRefusedException(
                root.TryGetProperty("error", out var error) ? error.GetString() : response.StatusCode.ToString(),
                root.TryGetProperty("error_description", out var description) ? description.GetString() : null);
        }

        string Required(string name) => root.TryGetProperty(name, out var value)
            ? value.ToString()
            : throw new InvalidOperationException($"Resposta sem {name}: {string.Join(", ", root.EnumerateObject().Select(property => property.Name))}");

        return new TokenSet(Required("access_token"), Required("refresh_token"), int.Parse(Required("expires_in"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Encode(Dictionary<string, string> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static Dictionary<string, string> Parse(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record TokenSet(string AccessToken, string RefreshToken, int ExpiresIn);

internal sealed class OAuthRefusedException(string? error, string? description)
    : Exception($"{error}: {description}")
{
    public string? Error { get; } = error;
}

/// <summary>Um desktop conectado: o id do dispositivo e os tokens.</summary>
internal sealed class DesktopSession(ApiFactory api, Guid deviceId, TokenSet tokens)
{
    public const string RedirectUri = "http://127.0.0.1:47833/callback";

    public Guid DeviceId { get; } = deviceId;

    public TokenSet Tokens { get; set; } = tokens;

    public HttpClient Api()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
        return client;
    }

    public async Task<T> GetAsync<T>(string path)
    {
        using var client = Api();
        using var response = await client.GetAsync(path);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(SyncJson.Options))!;
    }

    public async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body)
    {
        using var client = Api();
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: SyncJson.Options) };
        using var response = await client.SendAsync(request);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<TResponse>(SyncJson.Options))!;
    }

    public async Task<Guid> WorkspaceAsync() => (await GetAsync<List<WorkspaceInfo>>(ApiRoutes.Workspaces)).Single().Id;

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);
        }
    }
}

/// <summary>O "Google" dos testes: o desafio já volta com a pessoa do cabeçalho autenticada.</summary>
internal sealed class FakeGoogleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SubjectHeader = "X-Test-Google-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var subject = Request.Headers[SubjectHeader].ToString();
        subject = subject.Length == 0 ? "google-1" : subject;

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClaimTypes.Email, $"{subject}@gmail.com"),
                new Claim(ClaimTypes.Name, $"Pessoa {subject}"),
            ],
            Scheme.Name);

        await Context.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(identity), properties);
        Response.Redirect(properties.RedirectUri!);
    }
}

/// <summary>Tudo o que o servidor escreveu no log, para procurar segredo.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join(Environment.NewLine, _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category} {formatter(state, exception)} {exception}");
    }
}
