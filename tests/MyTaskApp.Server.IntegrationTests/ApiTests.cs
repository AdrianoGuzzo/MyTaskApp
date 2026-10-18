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
    public async Task ARevokedAccessToken_Is401RightAway()
    {
        var session = await _api.LoginAsync("ana");
        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);

        await _api.RevokeAsync(session.Tokens.AccessToken, "access_token");

        using var client = session.Api();
        (await client.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokingTheRefreshToken_LeavesTheAccessTokenAlive()
    {
        var session = await _api.LoginAsync("ana");

        await _api.RevokeAsync(session.Tokens.RefreshToken);

        // O OpenIddict revoga só o token pedido: por isso o "sair" do app revoga os dois (SEC-010).
        using var client = session.Api();
        (await client.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoggingInAgainOnTheSameDevice_DropsTheOldAccessTokenRightAway()
    {
        var device = Guid.CreateVersion7();
        var before = await _api.LoginAsync("ana", device);

        var after = await _api.LoginAsync("ana", device);

        // A autorização anterior foi revogada; sem a validação do token (SEC-010), o access dela seguia aceito.
        using (var client = before.Api())
        {
            (await client.GetAsync(ApiRoutes.Me, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await after.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(device);
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

        // 403 com o código, e não 401: é assim que o app sabe que precisa de um id novo (SEC-010 não pode tirar isso).
        using (var client = laptop.Api())
        using (var me = await client.GetAsync(ApiRoutes.Me, Ct))
        {
            me.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await me.Content.ReadAsStringAsync(Ct)).Should().Contain(SyncErrorCodes.DeviceRevoked);
        }

        var refresh = () => _api.RefreshAsync(laptop.Tokens.RefreshToken);
        var again = () => _api.AuthorizeAsync("ana", laptop.DeviceId);

        (await refresh.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be("invalid_grant");
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
    public async Task ATaskWithAThousandOccurrences_ComesPageByPage_WithoutTheWholeFamilyEachTime()
    {
        // SEC-005: cada página de limit=1 devolvia ~1.000 mudanças (~480 KB), e o cursor andava uma por vez.
        await using var api = await CreateApiAsync(new() { ["Sync:RateLimits:SyncPerMinute"] = "100000", ["Sync:RateLimits:SyncBurst"] = "100000" });
        var a = await api.LoginAsync("ana");
        var workspace = await a.WorkspaceAsync();
        var task = await PushTaskWithOccurrencesAsync(a, workspace, 1_000);
        await PushAsync(a, workspace, [Upsert(a, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Renomeada") })]);

        var pages = await PullAllAsync(a, workspace, limit: 1);
        var changes = pages.SelectMany(page => page.Changes).ToList();

        changes.Select(change => change.EntityId).Distinct().Should().HaveCount(1_001);
        changes.Should().HaveCountLessThanOrEqualTo(1_002);
        pages.Should().OnlyContain(page => page.Changes.Count <= 2);
    }

    [Fact]
    public async Task ThePullPage_RespectsTheConfiguredCeiling()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:Pull:MaxEntities"] = "50" });
        var a = await api.LoginAsync("ana");
        var workspace = await a.WorkspaceAsync();
        await PushTaskWithOccurrencesAsync(a, workspace, 1_000);

        var pages = await PullAllAsync(a, workspace, limit: SyncSchema.MaxPullLimit);
        var changes = pages.SelectMany(page => page.Changes).ToList();

        pages.Should().OnlyContain(page => page.Changes.Count <= 50);
        changes.Select(change => change.EntityId).Distinct().Should().HaveCount(1_001);
        changes.Should().HaveCountLessThanOrEqualTo(1_002);
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

    /// <summary>SEC-017 (T34): a versão base é uma que o servidor deu, nem negativa nem futura.</summary>
    [Fact]
    public async Task ABaseVersionTheServerNeverGave_IsRefused()
    {
        var session = await _api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();
        var task = Guid.CreateVersion7();
        await PushAsync(session, workspace, [Upsert(session, SyncEntityTypes.Task, task, 0, TaskFields("Pagar boleto"))]);

        var push = await session.SendAsync<PushRequest, PushResponse>(HttpMethod.Post, ApiRoutes.Push(workspace), new PushRequest(
            SyncSchema.Current,
            session.DeviceId,
            [
                Upsert(session, SyncEntityTypes.Task, task, 1_000_000_000_000, new() { [SyncFields.Task.Title] = SyncValue.String("Do futuro") }),
                Upsert(session, SyncEntityTypes.Task, task, -5, new() { [SyncFields.Task.Title] = SyncValue.String("Negativa") }),
            ]));

        push.Results.Should().HaveCount(2).And.OnlyContain(result =>
            result.Outcome == OperationOutcome.Rejected && result.Error!.Code == SyncErrorCodes.InvalidBaseVersion && result.Version == 1);
        var change = (await session.GetAsync<PullResponse>(ApiRoutes.Pull(workspace))).Changes.Should().ContainSingle().Subject;
        change.Version.Should().Be(1);
        change.Fields[SyncFields.Task.Title].GetString().Should().Be("Pagar boleto");
        _api.Logs.Text.Should().NotContain("SyncServerUnhandled");
    }

    /// <summary>
    /// SEC-008: membro nulo e caractere NUL nunca chegam a 500. O que é forma
    /// do pedido vira 400; o NUL num campo recusa só a operação, para um texto
    /// colado no desktop não travar o resto do lote. O NUL na plataforma do
    /// dispositivo sai do texto (SEC-015): veja o teste do ESC, logo abaixo.
    /// </summary>
    [Theory]
    [InlineData("fetch entities null")]
    [InlineData("push operations null")]
    [InlineData("push operations missing")]
    [InlineData("push operation null")]
    [InlineData("push entityType null")]
    [InlineData("push fields null")]
    [InlineData("push nul in a known field")]
    [InlineData("push nul in an unknown field")]
    [InlineData("push nul in an unknown field name")]
    public async Task NullMembersAndNulCharacters_NeverBecomeA500(string body)
    {
        var session = await _api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();
        var device = session.DeviceId;
        var task = Guid.CreateVersion7();
        var push = $$"""{"schemaVersion":{{SyncSchema.Current}},"deviceId":"{{device}}" """;
        string Operation(string entityType, string fields) =>
            $$"""{"opId":"{{Guid.CreateVersion7()}}","deviceId":"{{device}}","entityType":{{entityType}},"entityId":"{{task}}","kind":"upsert","baseVersion":0,"fields":{{fields}}}""";

        using var client = session.Api();
        using var response = body switch
        {
            "fetch entities null" => await PostJsonAsync(client, ApiRoutes.Fetch(workspace), """{"entities":null}"""),
            "push operations null" => await PostJsonAsync(client, ApiRoutes.Push(workspace), push + ""","operations":null}"""),
            "push operations missing" => await PostJsonAsync(client, ApiRoutes.Push(workspace), push + "}"),
            "push operation null" => await PostJsonAsync(client, ApiRoutes.Push(workspace), push + ""","operations":[null]}"""),
            "push entityType null" => await PostJsonAsync(client, ApiRoutes.Push(workspace), push + $$""","operations":[{{Operation("null", "{}")}}]}"""),
            "push fields null" => await PostJsonAsync(client, ApiRoutes.Push(workspace), push + $$""","operations":[{{Operation($"\"{SyncEntityTypes.Task}\"", "null")}}]}"""),
            "push nul in a known field" => await PushAsync(TaskFields("titulo\0nul")),
            "push nul in an unknown field" => await PushAsync(new(TaskFields("Pagar boleto")) { ["colorOfTheFuture"] = SyncValue.String("a\0b") }),
            "push nul in an unknown field name" => await PushAsync(new(TaskFields("Pagar boleto")) { ["color\0OfTheFuture"] = SyncValue.String("ab") }),
            _ => throw new ArgumentOutOfRangeException(nameof(body)),
        };

        if (body.StartsWith("push nul", StringComparison.Ordinal))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var result = (await response.Content.ReadFromJsonAsync<PushResponse>(SyncJson.Options, Ct))!.Results.Single();
            result.Outcome.Should().Be(OperationOutcome.Rejected);

            // O campo que o contrato não conhece é recusado antes de olhar o valor (ADR-072).
            result.Error!.Code.Should().Be(body.Contains("unknown", StringComparison.Ordinal) ? SyncErrorCodes.UnknownField : SyncErrorCodes.ValidationFailed);
            result.Error.Message.Should().NotContain("\0");
            (await session.GetAsync<PullResponse>(ApiRoutes.Pull(workspace))).Changes.Should().BeEmpty();
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SyncErrorCodes.BadRequest);
        }

        _api.Logs.Text.Should().NotContain("SyncServerUnhandled");

        Task<HttpResponseMessage> PushAsync(Dictionary<string, JsonElement> fields) =>
            client.PostAsync(
                ApiRoutes.Push(workspace),
                JsonContent.Create(
                    new PushRequest(SyncSchema.Current, device, [new SyncOperation(Guid.CreateVersion7(), device, SyncEntityTypes.Task, task, SyncOperationKind.Upsert, 0, fields, Created)]),
                    options: SyncJson.Options),
                Ct);
    }

    /// <summary>
    /// SEC-015: o 21º dispositivo ativo é recusado com código próprio, e nada
    /// é gravado. Entrar de novo num que já existe passa; revogar um libera a vaga.
    /// </summary>
    [Fact]
    public async Task TheTwentyFirstActiveDevice_IsRefused_AndRevokingOneFreesTheSlot()
    {
        var devices = new List<DesktopSession>();

        for (var i = 0; i < 20; i++)
        {
            devices.Add(await _api.LoginAsync("ana", deviceName: $"PC-{i:00}"));
        }

        var newcomer = Guid.CreateVersion7();
        var refused = () => _api.AuthorizeAsync("ana", newcomer);

        var refusal = (await refused.Should().ThrowAsync<OAuthRefusedException>()).Which;
        refusal.Error.Should().Be(SyncErrorCodes.DeviceLimitReached);
        refusal.Description.Should().Contain("20").And.Contain("Revogue");

        var again = await _api.LoginAsync("ana", devices[0].DeviceId);
        (await again.GetAsync<List<DeviceInfo>>(ApiRoutes.Devices)).Should().HaveCount(20).And.NotContain(device => device.DeviceId == newcomer);

        await again.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(devices[1].DeviceId), new { });
        var admitted = await _api.LoginAsync("ana", newcomer);

        var listed = await admitted.GetAsync<List<DeviceInfo>>(ApiRoutes.Devices);
        listed.Count(device => device.RevokedAt is null).Should().Be(20);
        listed.Should().Contain(device => device.DeviceId == newcomer);
    }

    [Fact]
    public async Task TheDeviceCap_ComesFromTheConfiguration_AndZeroTurnsItOff()
    {
        await using (var capped = await CreateApiAsync(new() { ["Sync:MaxActiveDevicesPerUser"] = "1" }))
        {
            await capped.LoginAsync("ana");
            var second = () => capped.AuthorizeAsync("ana", Guid.CreateVersion7());

            (await second.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be(SyncErrorCodes.DeviceLimitReached);
        }

        await using var open = await CreateApiAsync(new() { ["Sync:MaxActiveDevicesPerUser"] = "0" });

        for (var i = 0; i < 22; i++)
        {
            await open.AuthorizeAsync("ana", Guid.CreateVersion7());
        }
    }

    /// <summary>
    /// SEC-015: o apelido sai sem o U+202E (que inverteria o nome na lista) e a
    /// plataforma sem o ESC das sequências ANSI nem o NUL (SEC-008), sem 400.
    /// </summary>
    [Fact]
    public async Task DeviceTexts_LoseControlAndFormatCharacters()
    {
        var session = await _api.LoginAsync("ana", deviceName: "<b>disp</b>‮exe.txt");

        var device = await session.SendAsync<DeviceHello, DeviceInfo>(
            HttpMethod.Put,
            ApiRoutes.CurrentDevice,
            new DeviceHello(null, "Win\u001b[2J\0", "1.0.0‏", SyncSchema.Current));

        device.Nickname.Should().Be("<b>disp</b>exe.txt");
        device.Platform.Should().Be("Win[2J");
        device.AppVersion.Should().Be("1.0.0");

        var listed = (await session.GetAsync<List<DeviceInfo>>(ApiRoutes.Devices)).Single();
        listed.Nickname.Should().NotContain("‮");
        listed.Platform.Should().NotContain("\u001b");
        _api.Logs.Text.Should().NotContain("SyncServerUnhandled");
    }

    [Fact]
    public async Task AnEndlessCapabilityList_IsRefused()
    {
        var session = await _api.LoginAsync("ana");
        using var client = session.Api();

        using var response = await client.PutAsync(
            ApiRoutes.CurrentDevice,
            JsonContent.Create(new DeviceHello(null, "Windows", "1.0.0", SyncSchema.Current, [.. Enumerable.Repeat("agente", 20_000)]), options: SyncJson.Options),
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SyncErrorCodes.BadRequest);
    }

    [Fact]
    public async Task TheEntityCap_ComesFromTheConfiguration()
    {
        await using var api = await CreateApiAsync(new() { ["Sync:Push:MaxEntityBytes"] = "4096" });
        var session = await api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();
        var small = TaskFields("Cabe");
        var big = TaskFields("Não cabe");
        big[SyncFields.Task.Description] = SyncValue.String(new string('x', 5_000));

        var push = await session.SendAsync<PushRequest, PushResponse>(HttpMethod.Post, ApiRoutes.Push(workspace), new PushRequest(
            SyncSchema.Current,
            session.DeviceId,
            [
                new SyncOperation(Guid.CreateVersion7(), session.DeviceId, SyncEntityTypes.Task, Guid.CreateVersion7(), SyncOperationKind.Upsert, 0, small, Created),
                new SyncOperation(Guid.CreateVersion7(), session.DeviceId, SyncEntityTypes.Task, Guid.CreateVersion7(), SyncOperationKind.Upsert, 0, big, Created),
            ]));

        push.Results.Select(result => result.Outcome).Should().Equal(OperationOutcome.Applied, OperationOutcome.Rejected);
        push.Results[1].Error!.Code.Should().Be(SyncErrorCodes.EntityTooLarge);
    }

    [Fact]
    public async Task AGuidInAnotherFormat_DoesNotDodgeTheUniqueKey_AndAnInstantNeedsAnOffset()
    {
        // SEC-014: tagId em D, N e B com o mesmo alias eram aplicados os três.
        var session = await _api.LoginAsync("ana");
        var workspace = await session.WorkspaceAsync();
        var tag = Guid.CreateVersion7();
        var directories = new[] { "D", "N", "B" }.Select(format => (Id: Guid.CreateVersion7(), Format: format)).ToList();
        Dictionary<string, JsonElement> Directory(string tagId) => new()
        {
            [SyncFields.TagDirectory.TagId] = SyncValue.String(tagId),
            [SyncFields.TagDirectory.Alias] = SyncValue.String("repo"),
            [SyncFields.CreatedAt] = SyncValue.Instant(Created),
        };

        var push = await session.SendAsync<PushRequest, PushResponse>(HttpMethod.Post, ApiRoutes.Push(workspace), new PushRequest(
            SyncSchema.Current,
            session.DeviceId,
            [
                Upsert(session, SyncEntityTypes.Tag, tag, 0, new()
                {
                    [SyncFields.Tag.Name] = SyncValue.String("Casa"),
                    [SyncFields.Tag.ColorHex] = SyncValue.String("#00FF00"),
                    [SyncFields.CreatedAt] = SyncValue.Instant(Created),
                }),
                .. directories.Select(directory => Upsert(session, SyncEntityTypes.TagDirectory, directory.Id, 0, Directory(tag.ToString(directory.Format)))),
                Upsert(session, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, new(TaskFields("Sem fuso")) { [SyncFields.CreatedAt] = SyncValue.String("2026-10-13T12:00:00") }),
            ]));

        push.Results.Select(result => result.Error?.Code).Should().Equal(
            null, null, SyncErrorCodes.UniqueViolation, SyncErrorCodes.UniqueViolation, SyncErrorCodes.ValidationFailed);

        var pulled = (await session.GetAsync<PullResponse>(ApiRoutes.Pull(workspace))).Changes
            .Where(change => change.EntityType == SyncEntityTypes.TagDirectory)
            .Should().ContainSingle().Subject;
        pulled.EntityId.Should().Be(directories[0].Id);
        pulled.Fields[SyncFields.TagDirectory.TagId].GetString().Should().Be(tag.ToString("D"));
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

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    private static SyncOperation Upsert(DesktopSession session, string type, Guid id, long baseVersion, Dictionary<string, JsonElement> fields) =>
        new(Guid.CreateVersion7(), session.DeviceId, type, id, SyncOperationKind.Upsert, baseVersion, fields, Created);

    private static async Task PushAsync(DesktopSession session, Guid workspace, IReadOnlyList<SyncOperation> operations)
    {
        var push = await session.SendAsync<PushRequest, PushResponse>(
            HttpMethod.Post, ApiRoutes.Push(workspace), new PushRequest(SyncSchema.Current, session.DeviceId, operations));

        push.Results.Should().OnlyContain(result => result.Outcome == OperationOutcome.Applied);
    }

    /// <summary>Uma tarefa e as ocorrências dela, em pushes do tamanho máximo.</summary>
    private static async Task<Guid> PushTaskWithOccurrencesAsync(DesktopSession session, Guid workspace, int occurrences)
    {
        var task = Guid.CreateVersion7();
        var operations = new List<SyncOperation> { Upsert(session, SyncEntityTypes.Task, task, 0, TaskFields("Recorrente")) };
        operations.AddRange(Enumerable.Range(0, occurrences).Select(_ => Upsert(session, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, new()
        {
            [SyncFields.Occurrence.TaskId] = SyncValue.Guid(task),
            [SyncFields.Occurrence.Schedule] = SyncValue.Object((SyncFields.Occurrence.Date, SyncValue.Null), (SyncFields.Occurrence.Time, SyncValue.Null)),
            [SyncFields.Occurrence.State] = SyncValue.Object((SyncFields.Occurrence.Status, SyncValue.Integer(0)), (SyncFields.Occurrence.CompletedAt, SyncValue.Null)),
        })));

        foreach (var chunk in operations.Chunk(SyncSchema.MaxOperationsPerPush))
        {
            await PushAsync(session, workspace, chunk);
        }

        return task;
    }

    /// <summary>Do zero até o fim, pelo HTTP, conferindo que cada página anda o cursor.</summary>
    private static async Task<List<PullResponse>> PullAllAsync(DesktopSession session, Guid workspace, int limit)
    {
        var pages = new List<PullResponse>();
        var cursor = SyncCursor.Start;

        while (pages.Count < 5_000)
        {
            var page = await session.GetAsync<PullResponse>($"{ApiRoutes.Pull(workspace)}?cursor={Uri.EscapeDataString(cursor)}&limit={limit}");
            pages.Add(page);

            if (!page.HasMore)
            {
                return pages;
            }

            page.NextCursor.Should().NotBe(cursor);
            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("O pull não terminou.");
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
