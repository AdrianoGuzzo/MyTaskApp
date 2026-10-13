using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O cliente do desktop (ADR-061): o login pelo navegador com PKCE, a
/// renovação serializada, a classificação dos erros e o log sem segredo.
/// </summary>
public sealed class SyncClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeJiraServer _server = new();

    private readonly InMemorySecretStore _secrets = new();

    private readonly CapturingLog _log = new();

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeDeviceIdentity _device = new() { DefaultNickname = "Notebook" };

    private readonly SyncOptions _options = new() { ApiBaseUrl = "https://sync.exemplo.com", CallbackPort = FreePort() };

    private readonly SyncAuthenticationService _auth;

    private readonly SyncApiClient _api;

    private int _issued;

    public SyncClientTests()
    {
        var http = new SyncHttp(new HttpClient(_server), _options, _log.For<SyncHttp>());
        _auth = new SyncAuthenticationService(_options, http, _secrets, _device, _clock, _log.For<SyncAuthenticationService>());
        _api = new SyncApiClient(http, _auth);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _auth.Dispose();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://sync.exemplo.com", false)]
    [InlineData("https://sync.exemplo.com", true)]
    [InlineData("http://localhost:5280", true)]
    [InlineData("http://127.0.0.1:5280/", true)]
    public void OnlyHttpsOrLoopback_IsAServer(string? url, bool available)
    {
        new SyncOptions { ApiBaseUrl = url }.IsAvailable.Should().Be(available);
    }

    [Fact]
    public async Task WithoutAStoredLogin_ItAsksToReconnect()
    {
        var token = () => _auth.GetAccessTokenAsync(Ct);

        (await token.Should().ThrowAsync<SyncApiException>()).Which.Kind.Should().Be(SyncFailureKind.NeedsReconnect);
    }

    [Fact]
    public async Task TheRefreshTokenRotates_AndTheAccessTokenIsReusedUntilItExpires()
    {
        _secrets.Secrets[SyncOptions.SecretName] = "refresh-0";
        IssueTokensOnRefresh();

        var first = await _auth.GetAccessTokenAsync(Ct);
        var again = await _auth.GetAccessTokenAsync(Ct);

        first.Should().Be("access-1");
        again.Should().Be(first);
        _secrets.Secrets[SyncOptions.SecretName].Should().Be("refresh-1", "o novo refresh é gravado antes de usar o access");
        _server.Requests.Should().ContainSingle(request => request.Uri.AbsolutePath == ApiRoutes.Token)
            .Which.Body.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=refresh-0");

        _clock.Advance(TimeSpan.FromMinutes(15));

        (await _auth.GetAccessTokenAsync(Ct)).Should().Be("access-2");
    }

    [Fact]
    public async Task ConcurrentCallers_ShareASingleRefresh()
    {
        _secrets.Secrets[SyncOptions.SecretName] = "refresh-0";
        IssueTokensOnRefresh();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _auth.GetAccessTokenAsync(Ct)));

        tokens.Should().AllBe("access-1");
        _server.Requests.Count(request => request.Uri.AbsolutePath == ApiRoutes.Token).Should().Be(1);
    }

    [Fact]
    public async Task ARefreshTokenThatDied_IsForgotten_AndAsksToReconnect()
    {
        _secrets.Secrets[SyncOptions.SecretName] = "refresh-revogado";
        _server.On(ApiRoutes.Token, HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"revogado"}""");

        var token = () => _auth.GetAccessTokenAsync(Ct);

        (await token.Should().ThrowAsync<SyncApiException>()).Which.Kind.Should().Be(SyncFailureKind.NeedsReconnect);
        _secrets.Secrets.Should().NotContainKey(SyncOptions.SecretName);
    }

    [Fact]
    public async Task A401_RenewsTheTokenAndTriesOnce()
    {
        _secrets.Secrets[SyncOptions.SecretName] = "refresh-0";
        IssueTokensOnRefresh();
        _server.On(ApiRoutes.Me, request => request.Headers.Authorization!.Parameter == "access-1"
            ? FakeJiraServer.Json(HttpStatusCode.Unauthorized, "{}")
            : FakeJiraServer.Json(HttpStatusCode.OK, """{"user":{"id":"019a1f2e-0000-7000-8000-000000000001","displayName":"Ana","email":null},"device":null}"""));

        var me = await _api.MeAsync(Ct);

        me.User.DisplayName.Should().Be("Ana");
        _server.Requests.Count(request => request.Uri.AbsolutePath == ApiRoutes.Me).Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", SyncFailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, "", SyncFailureKind.Transient)]
    [InlineData(HttpStatusCode.Gone, "cursor_ahead", SyncFailureKind.CursorAhead)]
    [InlineData(HttpStatusCode.UpgradeRequired, "upgrade_required", SyncFailureKind.UpgradeRequired)]
    [InlineData(HttpStatusCode.Forbidden, "device_revoked", SyncFailureKind.DeviceRevoked)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", SyncFailureKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, "not_found", SyncFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", SyncFailureKind.Rejected)]
    public async Task ServerAnswers_BecomeTheRightKindOfFailure(HttpStatusCode status, string code, SyncFailureKind kind)
    {
        await ConnectedAsync();
        _server.On("/sync/pull", request =>
        {
            var response = FakeJiraServer.Json(status, $$"""{"title":"recusado","code":"{{code}}"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var pull = () => _api.PullAsync(Guid.CreateVersion7(), "", 100, Ct);

        var failure = (await pull.Should().ThrowAsync<SyncApiException>()).Which;
        failure.Kind.Should().Be(kind);

        if (kind == SyncFailureKind.Transient)
        {
            failure.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        }
    }

    [Fact]
    public async Task WithoutNetwork_ItIsTransient()
    {
        await ConnectedAsync();
        _server.Offline = true;

        var pull = () => _api.PullAsync(Guid.CreateVersion7(), "", 100, Ct);

        (await pull.Should().ThrowAsync<SyncApiException>()).Which.Kind.Should().Be(SyncFailureKind.Transient);
    }

    [Fact]
    public async Task TheBrowserLogin_UsesPkceAndTheLoopback_AndStoresOnlyTheRefreshToken()
    {
        string? challenge = null;
        _server.On(ApiRoutes.Token, request =>
        {
            var form = Form(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var verifier = form["code_verifier"];
            var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            return form["code"] == "codigo-do-navegador" && expected == challenge
                ? FakeJiraServer.Json(HttpStatusCode.OK, """{"access_token":"access-login","refresh_token":"refresh-login","expires_in":900}""")
                : FakeJiraServer.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        });

        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var query = Form(authorization.AuthorizeUrl.Query.TrimStart('?'));
        challenge = query["code_challenge"];

        authorization.AuthorizeUrl.AbsolutePath.Should().Be(ApiRoutes.Authorize);
        query["code_challenge_method"].Should().Be("S256");
        query["client_id"].Should().Be(SyncSchema.DesktopClientId);
        query["redirect_uri"].Should().Be($"http://127.0.0.1:{_options.CallbackPort}/callback");
        query[SyncSchema.DeviceIdClaim].Should().Be(_device.DeviceId.ToString());
        query[SyncSchema.DeviceNameParameter].Should().Be("Notebook");
        query.Should().NotContainKey("client_secret");

        using var browser = new HttpClient();
        await browser.GetAsync($"http://127.0.0.1:{_options.CallbackPort}/callback?state={query["state"]}&code=codigo-do-navegador", Ct);
        await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _secrets.Secrets[SyncOptions.SecretName].Should().Be("refresh-login");
        (await _auth.GetAccessTokenAsync(Ct)).Should().Be("access-login");
    }

    [Fact]
    public async Task ARevokedDevice_IsToldAsSuch_WhenLoggingInAgain()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var state = Form(authorization.AuthorizeUrl.Query.TrimStart('?'))["state"];

        using var browser = new HttpClient();
        await browser.GetAsync($"http://127.0.0.1:{_options.CallbackPort}/callback?state={state}&error=device_revoked", Ct);

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await completion.Should().ThrowAsync<SyncApiException>()).Which.Kind.Should().Be(SyncFailureKind.DeviceRevoked);
    }

    [Fact]
    public async Task SigningOut_RevokesAtTheServer_AndForgetsLocally_EvenOffline()
    {
        await ConnectedAsync();
        _server.On(ApiRoutes.Revocation, HttpStatusCode.OK, "");

        await _auth.SignOutAsync(Ct);

        _server.Requests.Should().Contain(request => request.Uri.AbsolutePath == ApiRoutes.Revocation && request.Body!.Contains("token_type_hint=refresh_token"));
        _secrets.Secrets.Should().BeEmpty();
        (await _auth.HasCredentialsAsync(Ct)).Should().BeFalse();

        await ConnectedAsync();
        _server.Offline = true;
        await _auth.SignOutAsync(Ct);
        _secrets.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLogNeverCarriesTokensOrTheQuery()
    {
        await ConnectedAsync();
        _server.On("/sync/pull", HttpStatusCode.OK, """{"changes":[],"nextCursor":"MS4x","hasMore":false}""");

        await _api.PullAsync(Guid.CreateVersion7(), "cursor-secreto", 100, Ct);

        _log.Lines.Should().Contain(line => line.Contains("/sync/pull", StringComparison.Ordinal));
        foreach (var secret in new[] { "access-1", "refresh-0", "refresh-1", "cursor-secreto" })
        {
            _log.Mentions(secret).Should().BeFalse(secret);
        }
    }

    private async Task ConnectedAsync()
    {
        _secrets.Secrets[SyncOptions.SecretName] = "refresh-0";
        _issued = 0;
        IssueTokensOnRefresh();
        await _auth.GetAccessTokenAsync(Ct);
    }

    /// <summary>Cada renovação devolve um par novo: access-N e refresh-N.</summary>
    private void IssueTokensOnRefresh() => _server.On(ApiRoutes.Token, _ =>
    {
        var n = Interlocked.Increment(ref _issued);
        return FakeJiraServer.Json(HttpStatusCode.OK, $$"""{"access_token":"access-{{n}}","refresh_token":"refresh-{{n}}","expires_in":900}""");
    });

    private static Dictionary<string, string> Form(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
