using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using MyTaskApp.Server.Api;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O issuer fixo (SEC-007): o <c>Host</c> da requisição não muda o issuer do
/// discovery nem o dos tokens, e com o <c>AllowedHosts</c> restrito o
/// <c>Host</c> forjado leva 400. Em Development, só os nomes do loopback. Fora
/// dele, sem issuer ou com <c>AllowedHosts</c> aberto, o servidor não sobe.
/// </summary>
public sealed class IssuerTests(PostgresFixture postgres)
{
    private const string Issuer = "https://sync.test/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithAllowedHosts_AForgedHostIsRefused_AndThePublicNameGetsTheFixedIssuer()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:Issuer"] = Issuer, ["AllowedHosts"] = "sync.test;localhost" });

        using var forged = Client(api, "http://evil.example/");
        using var refused = await forged.GetAsync("/.well-known/openid-configuration", Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var discovery = await DiscoveryAsync(api, Issuer);

        discovery.GetProperty("issuer").GetString().Should().Be(Issuer);
        discovery.GetProperty("authorization_endpoint").GetString().Should().Be(Issuer + "connect/authorize");
        discovery.GetProperty("token_endpoint").GetString().Should().Be(Issuer + "connect/token");
    }

    [Fact]
    public async Task WithAFixedIssuer_TheDiscoveryIssuerIgnoresTheHost()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:Issuer"] = Issuer });

        var discovery = await DiscoveryAsync(api, "http://evil.example/");

        // Os endpoints o OpenIddict monta sobre o endereço da requisição; quem barra o Host forjado neles é o AllowedHosts.
        discovery.GetProperty("issuer").GetString().Should().Be(Issuer);
    }

    [Fact]
    public async Task WithAFixedIssuer_TheTokenIsValidWhicheverHostTheClientUses()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:Issuer"] = Issuer });
        var session = await api.LoginAsync("ana");

        (await MeStatusAsync(api, "http://localhost/", session.Tokens.AccessToken)).Should().Be(HttpStatusCode.OK);
        (await MeStatusAsync(api, "http://192.168.100.8:5280/", session.Tokens.AccessToken)).Should().Be(HttpStatusCode.OK);

        using var other = Client(api, "http://192.168.100.8:5280/");
        using var refresh = await other.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.Tokens.RefreshToken,
            ["client_id"] = SyncSchema.DesktopClientId,
        }), Ct);

        refresh.StatusCode.Should().Be(HttpStatusCode.OK, "o refresh emitido por um host vale no outro");
    }

    [Fact]
    public async Task WithoutAFixedIssuer_TheHostDecides_AndTheTokenFailsOnAnotherHost()
    {
        await using var api = await CreateApiAsync();
        var session = await api.LoginAsync("ana");

        (await MeStatusAsync(api, "http://192.168.100.8:5280/", session.Tokens.AccessToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("http://localhost:5280/", HttpStatusCode.OK)]
    [InlineData("http://127.0.0.1:5280/", HttpStatusCode.OK)]
    [InlineData("http://[::1]:5280/", HttpStatusCode.OK)]
    [InlineData("http://evil.example/", HttpStatusCode.BadRequest)]
    [InlineData("http://192.168.100.8:5280/", HttpStatusCode.BadRequest)]
    public async Task InDevelopment_OnlyTheLoopbackNamesAreAccepted(string baseAddress, HttpStatusCode expected)
    {
        await using var api = await CreateApiAsync(environment: Environments.Development);

        using var client = Client(api, baseAddress);
        using var response = await client.GetAsync("/health", Ct);

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "sync.test", "Falta Sync:Issuer*")]
    [InlineData("http://sync.test/", "sync.test", "Sync:Issuer precisa ser https*")]
    [InlineData("https://sync.test/", null, "AllowedHosts precisa listar o nome público*")]
    [InlineData("https://sync.test/", "*", "AllowedHosts precisa listar o nome público*")]
    [InlineData("https://sync.test/", "outro.test", "O nome do Sync:Issuer (sync.test) precisa estar no AllowedHosts*")]
    [InlineData("https://sync.test/?a=1", "sync.test", "Sync:Issuer precisa ser um endereço absoluto*")]
    [InlineData("https://sync.test/", "localhost;sync.test", "Falta o certificado do Data Protection*")]
    [InlineData("https://a.sync.test/", "*.sync.test", "Falta o certificado do Data Protection*")]
    public async Task InProduction_TheIssuerAndAllowedHostsAreRequired(string? issuer, string? allowedHosts, string message)
    {
        // Com tudo certo, o próximo a faltar é um certificado: a conferência do endereço passou.
        await using var api = await CreateApiAsync(
            new() { ["Sync:Issuer"] = issuer, ["AllowedHosts"] = allowedHosts },
            Environments.Production);

        var start = () => api.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task TheGoogleRedirect_UsesThePublicName()
    {
        await using var api = await CreateApiAsync(
            new()
            {
                ["Sync:Issuer"] = Issuer,
                ["AllowedHosts"] = "sync.test",
                ["Authentication:Google:ClientId"] = "cliente-google",
                ["Authentication:Google:ClientSecret"] = "segredo-google",
            },
            fakeGoogle: false);

        using var browser = Client(api, Issuer);
        using var login = await browser.GetAsync("/account/login", Ct);

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.Host.Should().Be("accounts.google.com");
        Uri.UnescapeDataString(login.Headers.Location.Query).Should().Contain("redirect_uri=https://sync.test/signin-google&");

        using var forged = Client(api, "https://evil.example/");
        using var refused = await forged.GetAsync("/account/login", Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static HttpClient Client(ApiFactory api, string baseAddress) =>
        api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false });

    private static async Task<JsonElement> DiscoveryAsync(ApiFactory api, string baseAddress)
    {
        using var client = Client(api, baseAddress);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration", Ct));
        return document.RootElement.Clone();
    }

    private static async Task<HttpStatusCode> MeStatusAsync(ApiFactory api, string baseAddress, string accessToken)
    {
        using var client = Client(api, baseAddress);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync(ApiRoutes.Me, Ct);
        return response.StatusCode;
    }

    private async Task<ApiFactory> CreateApiAsync(
        Dictionary<string, string?>? settings = null,
        string environment = ServerSetup.TestingEnvironment,
        bool fakeGoogle = true) =>
        new(await postgres.CreateDatabaseAsync(Ct), settings, environment, fakeGoogle);
}
