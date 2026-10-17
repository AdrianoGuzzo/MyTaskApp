using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// A API de verdade, pelo HTTP (ADR-061): o login do desktop com PKCE, os
/// tokens, a revogação, o isolamento e o que nunca pode ir para o log.
/// </summary>
public sealed class ApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _api = await CreateApiAsync();

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheDesktopLogsInThroughTheBrowser_AndTheApiKnowsWhoAndWhichDevice()
    {
        var session = await _api.LoginAsync("ana", deviceName: "Notebook da Ana");

        var me = await session.GetAsync<MeResponse>(ApiRoutes.Me);
        var workspaces = await session.GetAsync<List<WorkspaceInfo>>(ApiRoutes.Workspaces);

        me.User.DisplayName.Should().Be("Pessoa ana");
        me.Device!.DeviceId.Should().Be(session.DeviceId);
        me.Device.Nickname.Should().Be("Notebook da Ana");
        workspaces.Should().ContainSingle().Which.Should().Match<WorkspaceInfo>(workspace => workspace.IsPersonal && workspace.Role == WorkspaceRole.Owner);
        session.Tokens.ExpiresIn.Should().BeLessThanOrEqualTo(15 * 60);
    }

    [Fact]
    public async Task LoggingInAgain_IsTheSameAccount()
    {
        var first = await _api.LoginAsync("ana");
        var second = await _api.LoginAsync("ana");

        (await second.GetAsync<MeResponse>(ApiRoutes.Me)).User.Id
            .Should().Be((await first.GetAsync<MeResponse>(ApiRoutes.Me)).User.Id);
        (await second.WorkspaceAsync()).Should().Be(await first.WorkspaceAsync());
    }

    [Fact]
    public async Task WithoutTheVerifier_TheCodeIsWorthless()
    {
        var (code, _) = await _api.AuthorizeAsync("ana", Guid.CreateVersion7());

        var exchange = () => _api.ExchangeAsync(code, verifier: null);

        await exchange.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task AWrongVerifier_IsRefused()
    {
        var (code, _) = await _api.AuthorizeAsync("ana", Guid.CreateVersion7());

        var exchange = () => _api.ExchangeAsync(code, verifier: "outro-verificador-com-mais-de-quarenta-e-tres-caracteres-aqui");

        (await exchange.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task AuthorizingWithoutPkce_IsRefused()
    {
        var authorize = () => _api.AuthorizeAsync("ana", Guid.CreateVersion7(), withPkce: false);

        await authorize.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task OnlyS256_IsOffered()
    {
        using var client = _api.CreateClient();
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration", Ct));

        discovery.RootElement.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(method => method.GetString()).Should().Equal("S256");
        discovery.RootElement.GetProperty("grant_types_supported").EnumerateArray()
            .Select(grant => grant.GetString()).Should().BeEquivalentTo("authorization_code", "refresh_token");
    }

    [Fact]
    public async Task ARedirectThatWasNotRegistered_NeverReceivesACode()
    {
        var authorize = () => _api.AuthorizeAsync("ana", Guid.CreateVersion7(), redirectUri: "http://127.0.0.1:9999/callback");

        await authorize.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task ThePhoneLogsInThroughItsOwnClient_AndComesBackByTheAppScheme()
    {
        var session = await _api.LoginMobileAsync("ana", deviceName: "Pixel da Ana");

        var me = await session.GetAsync<MeResponse>(ApiRoutes.Me);

        me.Device!.DeviceId.Should().Be(session.DeviceId);
        me.Device.Nickname.Should().Be("Pixel da Ana");
        (await session.WorkspaceAsync()).Should().Be(await (await _api.LoginAsync("ana")).WorkspaceAsync(), "celular e desktop da mesma pessoa caem no mesmo workspace");
    }

    [Fact]
    public async Task ThePhoneClient_NeverReturnsToTheLoopback()
    {
        var authorize = () => _api.AuthorizeAsync("ana", Guid.CreateVersion7(), clientId: SyncSchema.MobileClientId);

        await authorize.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task TheDesktopClient_NeverReturnsToTheAppScheme()
    {
        var authorize = () => _api.AuthorizeAsync("ana", Guid.CreateVersion7(), redirectUri: SyncSchema.MobileRedirectUri);

        await authorize.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task APhoneCode_IsWorthlessInTheDesktopClient()
    {
        var (code, verifier) = await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), redirectUri: SyncSchema.MobileRedirectUri, clientId: SyncSchema.MobileClientId);

        var exchange = () => _api.ExchangeAsync(code, verifier, SyncSchema.MobileRedirectUri, SyncSchema.DesktopClientId);

        await exchange.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task ThePhoneRefreshRotates_AndOnlyWorksForThePhoneClient()
    {
        var session = await _api.LoginMobileAsync("ana");

        var asDesktop = () => _api.RefreshAsync(session.Tokens.RefreshToken, SyncSchema.DesktopClientId);
        await asDesktop.Should().ThrowAsync<OAuthRefusedException>();

        var renewed = await _api.RefreshAsync(session.Tokens.RefreshToken, SyncSchema.MobileClientId);

        renewed.RefreshToken.Should().NotBe(session.Tokens.RefreshToken);
        session.Tokens = renewed;
        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);
    }

    [Fact]
    public async Task AConfiguredPhoneRedirect_IsAcceptedToo()
    {
        const string other = "io.github.adrianoguzzo.mytaskapp.dev://oauth/callback";
        await using var api = await CreateApiAsync(new() { ["Sync:MobileRedirectUris:1"] = other });

        var (code, verifier) = await api.AuthorizeAsync("ana", Guid.CreateVersion7(), redirectUri: other, clientId: SyncSchema.MobileClientId);
        var tokens = await api.ExchangeAsync(code, verifier, other, SyncSchema.MobileClientId);

        tokens.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ARefreshGivesNewTokens_AndTheOldRefreshTokenStopsWorking()
    {
        var session = await _api.LoginAsync("ana");

        var renewed = await _api.RefreshAsync(session.Tokens.RefreshToken);
        var reuse = () => _api.RefreshAsync(session.Tokens.RefreshToken);

        renewed.RefreshToken.Should().NotBe(session.Tokens.RefreshToken);
        (await reuse.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task ARevokedRefreshToken_CannotBeUsed()
    {
        var session = await _api.LoginAsync("ana");

        await _api.RevokeAsync(session.Tokens.RefreshToken);
        var refresh = () => _api.RefreshAsync(session.Tokens.RefreshToken);

        await refresh.Should().ThrowAsync<OAuthRefusedException>();
    }

    [Fact]
    public async Task AnExpiredAccessToken_Is401_AndTheRefreshedOneWorks()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:AccessTokenLifetime"] = "00:00:02" });
        var session = await api.LoginAsync("ana");

        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        using (var client = session.Api())
        {
            (await client.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        session.Tokens = await api.RefreshAsync(session.Tokens.RefreshToken);

        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);
    }

    [Fact]
    public async Task EveryApiRoute_NeedsABearerToken_AndTheLoginCookieIsNotEnough()
    {
        var routes = _api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Where(route => route.StartsWith(ApiRoutes.Prefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        routes.Should().HaveCountGreaterThan(8);

        // Um navegador com a sessão do Identity aberta: o cookie existe, e mesmo assim a API recusa.
        using var browser = _api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(FakeGoogleHandler.SubjectHeader, "ana");
        (await browser.GetAsync("/account/login?returnUrl=%2Fhealth", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var route in routes)
        {
            var path = route
                .Replace("{workspaceId:guid}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
                .Replace("{deviceId:guid}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal);

            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch })
            {
                using var response = await browser.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) }, Ct);
                response.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.MethodNotAllowed], $"{method} {path}");
            }
        }
    }

    [Fact]
    public async Task ARevokedDevice_LosesTheApiAndTheRefresh_AndCannotLogInAgainWithTheSameId()
    {
        var laptop = await _api.LoginAsync("ana", deviceName: "Notebook");
        var desktop = await _api.LoginAsync("ana", deviceName: "Desktop");

        await desktop.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(laptop.DeviceId), new { });

        using (var client = laptop.Api())
        {
            (await client.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }

        var refresh = () => _api.RefreshAsync(laptop.Tokens.RefreshToken);
        var again = () => _api.AuthorizeAsync("ana", laptop.DeviceId);

        await refresh.Should().ThrowAsync<OAuthRefusedException>();
        (await again.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be(SyncErrorCodes.DeviceRevoked);

        var devices = await desktop.GetAsync<List<DeviceInfo>>(ApiRoutes.Devices);
        devices.Single(device => device.DeviceId == laptop.DeviceId).RevokedAt.Should().NotBeNull();
        devices.Single(device => device.DeviceId == desktop.DeviceId).IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task TwoDevicesOfTheSameUser_ShareTheWorkspace_AndTheDataTravels()
    {
        var a = await _api.LoginAsync("ana");
        var b = await _api.LoginAsync("ana");
        var workspace = await a.WorkspaceAsync();
        var task = Guid.CreateVersion7();

        var push = await a.SendAsync<PushRequest, PushResponse>(HttpMethod.Post, ApiRoutes.Push(workspace), new PushRequest(
            SyncSchema.Current,
            a.DeviceId,
            [new SyncOperation(Guid.CreateVersion7(), a.DeviceId, SyncEntityTypes.Task, task, SyncOperationKind.Upsert, 0, TaskFields("Pagar boleto"), Created)]));
        var pull = await b.GetAsync<PullResponse>(ApiRoutes.Pull(workspace));

        push.Results.Single().Outcome.Should().Be(OperationOutcome.Applied);
        var change = pull.Changes.Should().ContainSingle().Subject;
        change.EntityId.Should().Be(task);
        change.CreatedByDeviceId.Should().Be(a.DeviceId);
        change.Fields[SyncFields.Task.Title].GetString().Should().Be("Pagar boleto");
    }

    [Fact]
    public async Task AnotherUser_GetsA404_ForMyWorkspace()
    {
        var ana = await _api.LoginAsync("ana");
        var bia = await _api.LoginAsync("bia");
        var workspace = await ana.WorkspaceAsync();

        using var client = bia.Api();
        var pull = await client.GetAsync(ApiRoutes.Pull(workspace), Ct);
        var push = await client.PostAsync(ApiRoutes.Push(workspace), JsonContent.Create(new PushRequest(SyncSchema.Current, bia.DeviceId, []), options: SyncJson.Options), Ct);

        pull.StatusCode.Should().Be(HttpStatusCode.NotFound);
        push.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await pull.Content.ReadAsStringAsync(Ct)).Should().Contain(SyncErrorCodes.NotFound);
    }

    [Fact]
    public async Task APushSignedForAnotherDevice_IsRefused()
    {
        var session = await _api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();

        using var client = session.Api();
        var response = await client.PostAsync(
            ApiRoutes.Push(workspace),
            JsonContent.Create(new PushRequest(SyncSchema.Current, Guid.CreateVersion7(), []), options: SyncJson.Options),
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SyncErrorCodes.DeviceMismatch);
    }

    [Fact]
    public async Task TooManyLoginRequests_Get429_WithRetryAfter()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:RateLimits:AuthPerMinute"] = "2" });
        using var client = api.CreateClient();

        var statuses = new List<HttpResponseMessage>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            statuses.Add(await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token" }), Ct));
        }

        var limited = statuses.Last();
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task TheApiQuotaIsPerAccount_EvenFromTheSameAddress()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:RateLimits:ApiPerMinute"] = "3" });
        var ana = await api.LoginAsync("ana");
        var bia = await api.LoginAsync("bia");
        using var anaClient = ana.Api();
        using var biaClient = bia.Api();

        for (var call = 0; call < 3; call++)
        {
            (await anaClient.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await anaClient.GetAsync(ApiRoutes.Me, Ct);
        using var other = await biaClient.GetAsync(ApiRoutes.Me, Ct);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await limited.Content.ReadAsStringAsync(Ct)).Should().Contain("rate_limited");
        other.StatusCode.Should().Be(HttpStatusCode.OK, "a cota da ana não é a da bia");
    }

    [Fact]
    public async Task TheSyncBucketIsPerAccount_EvenFromTheSameAddress()
    {
        await using var api = await CreateApiAsync(new()
        {
            ["Sync:RateLimits:SyncBurst"] = "2",
            ["Sync:RateLimits:SyncPerMinute"] = "1",
        });
        var ana = await api.LoginAsync("ana");
        var bia = await api.LoginAsync("bia");
        var anaPull = ApiRoutes.Pull(await ana.WorkspaceAsync());
        var biaPull = ApiRoutes.Pull(await bia.WorkspaceAsync());
        using var anaClient = ana.Api();
        using var biaClient = bia.Api();

        for (var call = 0; call < 2; call++)
        {
            (await anaClient.GetAsync(anaPull, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await anaClient.GetAsync(anaPull, Ct);
        using var other = await biaClient.GetAsync(biaPull, Ct);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RequestsWithoutAValidToken_HaveTheirOwnQuota_AndDoNotSpendTheAccounts()
    {
        await using var api = await CreateApiAsync(new()
        {
            ["Sync:RateLimits:ApiPerMinute"] = "3",
            ["Sync:RateLimits:AnonymousPerMinute"] = "2",
        });
        var ana = await api.LoginAsync("ana");
        using var anonymous = api.CreateClient();
        using var forged = api.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nao-e-um-token");

        using var withoutToken = await anonymous.GetAsync(ApiRoutes.Me, Ct);
        using var withBadToken = await forged.GetAsync(ApiRoutes.Me, Ct);
        using var overAnonymous = await anonymous.GetAsync(ApiRoutes.Me, Ct);

        withoutToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withBadToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        overAnonymous.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "o balde anônimo é do IP, separado");

        using var anaClient = ana.Api();

        for (var call = 0; call < 3; call++)
        {
            (await anaClient.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "os 401 não gastaram a cota da ana");
        }

        (await anaClient.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task TheGlobalCeiling_LimitsAnyRoute_PerAddress()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:RateLimits:GlobalPerMinute"] = "3" });
        using var client = api.CreateClient();

        for (var call = 0; call < 3; call++)
        {
            (await client.GetAsync("/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await client.GetAsync("/health", Ct);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task LoggingOut_NeedsTheAntiforgeryToken()
    {
        using var client = _api.CreateClient();

        var forged = await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheLogNeverCarriesATokenACodeOrAVerifier()
    {
        var (code, verifier) = await _api.AuthorizeAsync("ana", Guid.CreateVersion7());
        var tokens = await _api.ExchangeAsync(code, verifier);
        var renewed = await _api.RefreshAsync(tokens.RefreshToken);
        var session = new DesktopSession(_api, Guid.Empty, renewed);
        using (var client = session.Api())
        {
            await client.GetAsync(ApiRoutes.Me, Ct);
        }

        var log = _api.Logs.Text;

        log.Should().Contain("/connect/token", "o log de requisição está ligado");
        foreach (var secret in new[] { code, verifier, tokens.AccessToken, tokens.RefreshToken, renewed.AccessToken, renewed.RefreshToken })
        {
            log.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Health_IsPublic()
    {
        using var client = _api.CreateClient();

        (await client.GetAsync("/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<ApiFactory> CreateApiAsync(Dictionary<string, string?>? settings = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var api = new ApiFactory(connectionString, settings);
        _ = api.Server;
        return api;
    }

    private static Dictionary<string, JsonElement> TaskFields(string title) => new()
    {
        [SyncFields.Task.Title] = SyncValue.String(title),
        [SyncFields.Task.Priority] = SyncValue.Integer(1),
        [SyncFields.Task.Reminder] = SyncValue.Object(
            (SyncFields.Reminder.IsEnabled, SyncValue.Boolean(false)),
            (SyncFields.Reminder.Anchor, SyncValue.Integer(0)),
            (SyncFields.Reminder.Offset, SyncValue.Duration(TimeSpan.Zero)),
            (SyncFields.Reminder.Repeat, SyncValue.Boolean(false)),
            (SyncFields.Reminder.RepeatEvery, SyncValue.Duration(TimeSpan.FromMinutes(15))),
            (SyncFields.Reminder.Channels, SyncValue.Integer(1))),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };
}
