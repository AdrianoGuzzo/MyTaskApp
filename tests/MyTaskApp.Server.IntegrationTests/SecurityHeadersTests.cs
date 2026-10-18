using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Os headers de segurança (SEC-009): <c>no-store</c> no que tem dado de conta
/// ou de protocolo, <c>nosniff</c> em tudo, nada de <c>Server</c>, e a página do
/// logout sem frame e sem carregar nada.
/// </summary>
public sealed class SecurityHeadersTests(PostgresFixture postgres)
{
    private const string LogoutPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheApiResponses_AreNeverCached_AndNeverSniffed()
    {
        await using var api = await CreateApiAsync();
        var session = await api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();
        using var client = session.Api();

        using var me = await client.GetAsync(ApiRoutes.Me, Ct);
        using var devices = await client.GetAsync(ApiRoutes.Devices, Ct);
        using var pull = await client.GetAsync(ApiRoutes.Pull(workspace), Ct);
        using var fetch = await client.PostAsJsonAsync(ApiRoutes.Fetch(workspace), new FetchRequest([]), SyncJson.Options, Ct);

        foreach (var response in new[] { me, devices, pull, fetch })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, response.RequestMessage!.RequestUri!.AbsolutePath);
            ShouldNotBeStored(response);
            ShouldNotBeSniffed(response);
        }
    }

    [Fact]
    public async Task TheTokenEndpointAndTheErrors_AreNeverCached()
    {
        await using var api = await CreateApiAsync();
        var (code, verifier) = await api.AuthorizeAsync("ana", Guid.CreateVersion7());
        using var client = api.CreateClient();

        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = DesktopSession.RedirectUri,
            ["client_id"] = SyncSchema.DesktopClientId,
        }), Ct);
        using var unauthorized = await client.GetAsync(ApiRoutes.Me, Ct);
        using var notFound = await client.GetAsync("/api/v1/nao-existe", Ct);

        token.StatusCode.Should().Be(HttpStatusCode.OK);
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var response in new[] { token, unauthorized, notFound })
        {
            ShouldNotBeStored(response);
            ShouldNotBeSniffed(response);
        }
    }

    [Fact]
    public async Task DiscoveryJwksAndHealth_StayCacheable_ButAreNeverSniffed()
    {
        await using var api = await CreateApiAsync();
        using var client = api.CreateClient();

        using var discovery = await client.GetAsync("/.well-known/openid-configuration", Ct);
        using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(Ct));
        using var jwks = await client.GetAsync(new Uri(document.RootElement.GetProperty("jwks_uri").GetString()!).AbsolutePath, Ct);
        using var health = await client.GetAsync("/health", Ct);

        foreach (var response in new[] { discovery, jwks, health })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (response.Headers.CacheControl?.NoStore ?? false).Should().BeFalse(response.RequestMessage!.RequestUri!.AbsolutePath);
            ShouldNotBeSniffed(response);
        }
    }

    [Fact]
    public async Task TheLogoutPage_LoadsNothing_AndCannotBeFramed()
    {
        await using var api = await CreateApiAsync();
        using var client = api.CreateClient();

        using var page = await client.GetAsync("/account/logout", Ct);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.GetValues("Content-Security-Policy").Should().Equal(LogoutPolicy);
        page.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        ShouldNotBeStored(page);
        ShouldNotBeSniffed(page);
    }

    /// <summary>
    /// O TestServer nunca manda <c>Server</c>: aqui é o Kestrel de verdade, numa
    /// porta qualquer. O Kestrel põe o header em toda resposta, qualquer que seja
    /// a rota, então as anônimas bastam (o navegador do login não acha a porta).
    /// </summary>
    [Fact]
    public async Task OnKestrel_NoResponseNamesTheServer()
    {
        await using var api = await CreateApiAsync();
        api.UseKestrel(0);
        api.StartServer();
        using var client = api.CreateClient();

        var responses = new[]
        {
            await client.GetAsync(ApiRoutes.Me, Ct),
            await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password" }), Ct),
            await client.GetAsync("/health", Ct),
            await client.GetAsync("/.well-known/openid-configuration", Ct),
            await client.GetAsync("/account/logout", Ct),
            await client.GetAsync("/nao-existe", Ct),
        };

        responses[0].StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        responses[1].StatusCode.Should().Be(HttpStatusCode.BadRequest);
        responses[2].StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var response in responses)
        {
            using (response)
            {
                response.Headers.Server.Should().BeEmpty(response.RequestMessage!.RequestUri!.AbsolutePath);
                ShouldNotBeSniffed(response);
            }
        }
    }

    private static void ShouldNotBeStored(HttpResponseMessage response) =>
        (response.Headers.CacheControl?.NoStore ?? false).Should().BeTrue(response.RequestMessage!.RequestUri!.AbsolutePath);

    private static void ShouldNotBeSniffed(HttpResponseMessage response) =>
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal(["nosniff"], response.RequestMessage!.RequestUri!.AbsolutePath);

    private async Task<ApiFactory> CreateApiAsync() => new(await postgres.CreateDatabaseAsync(Ct));
}
