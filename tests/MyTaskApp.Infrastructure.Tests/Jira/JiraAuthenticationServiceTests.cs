using System.Net;
using System.Text;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Jira;

namespace MyTaskApp.Infrastructure.Tests.Jira;

/// <summary>
/// A conexão com o Jira (ADR-045): os dois caminhos, onde cada coisa é
/// guardada, e o que nunca pode aparecer no disco nem no log.
/// </summary>
public sealed class JiraAuthenticationServiceTests : IAsyncDisposable
{
    private readonly JiraHarness _jira = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _jira.DisposeAsync();

    [Fact]
    public async Task ANewInstall_IsDisconnected_AndSaysWhetherOAuthIsAvailable()
    {
        var connection = await _jira.Auth.GetConnectionAsync(Ct);

        connection.State.Should().Be(JiraConnectionState.Disconnected);
        connection.IsOAuthAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task ABuildWithoutTheOAuthApp_OffersOnlyTheApiToken()
    {
        await using var jira = new JiraHarness(withOAuth: false);

        (await jira.Auth.GetConnectionAsync(Ct)).IsOAuthAvailable.Should().BeFalse();

        var begin = () => jira.Auth.BeginAuthorizationAsync(Ct);
        await begin.Should().ThrowAsync<DomainException>().WithMessage("*API token*");
    }

    [Fact]
    public async Task ApiToken_ConnectsAfterTheJiraAcceptsIt()
    {
        var connection = await _jira.Auth.ConnectWithApiTokenAsync("empresa", "ana@empresa.com", JiraHarness.ApiToken, Ct);

        connection.State.Should().Be(JiraConnectionState.Connected);
        connection.Method.Should().Be(JiraAuthMethod.ApiToken);
        connection.SiteUrl.Should().Be(JiraHarness.Site);
        connection.AccountName.Should().Be("Ana Dev");
        connection.AccountEmail.Should().Be("ana@empresa.com");

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"ana@empresa.com:{JiraHarness.ApiToken}"));
        _jira.Server.Requests.Single().Authorization.Should().Be(expected);
        _jira.Server.Requests.Single().Uri.ToString().Should().Be($"{JiraHarness.Site}/rest/api/3/myself");
    }

    [Fact]
    public async Task ApiToken_RefusedByJira_SavesNothing()
    {
        _jira.Server.On("/rest/api/3/myself", HttpStatusCode.Unauthorized);

        var connect = () => _jira.ConnectWithApiTokenAsync(Ct);

        await connect.Should().ThrowAsync<DomainException>().WithMessage("*e-mail e token*");
        _jira.Secrets.Secrets.Should().BeEmpty();
        _jira.ConnectionFileText.Should().BeEmpty();
    }

    [Fact]
    public async Task ApiToken_Offline_ExplainsWithoutTechnicalDetail()
    {
        _jira.Server.Offline = true;

        var connect = () => _jira.ConnectWithApiTokenAsync(Ct);

        var failure = await connect.Should().ThrowAsync<DomainException>();
        failure.Which.Message.Should().Contain("conexão").And.NotContain("Host desconhecido");
    }

    [Theory]
    [InlineData("empresa", "https://empresa.atlassian.net/")]
    [InlineData("empresa.atlassian.net", "https://empresa.atlassian.net/")]
    [InlineData("https://empresa.atlassian.net/browse/GAECO-1234", "https://empresa.atlassian.net/")]
    [InlineData("  https://empresa.atlassian.net/  ", "https://empresa.atlassian.net/")]
    public void WhateverTheUserPastes_BecomesTheSite(string input, string site)
    {
        JiraAuthenticationService.NormalizeSite(input)!.ToString().Should().Be(site);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://empresa.atlassian.net")]
    [InlineData("ftp://empresa")]
    public void AnUnsafeOrEmptySite_IsRefused(string input)
    {
        JiraAuthenticationService.NormalizeSite(input).Should().BeNull();
    }

    [Fact]
    public async Task TheToken_GoesToTheVault_AndNeverToTheConnectionFile()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        _jira.Secrets.Secrets[JiraAuthenticationService.SecretName].Should().Contain(JiraHarness.ApiToken);
        _jira.ConnectionFileText.Should().NotBeEmpty().And.NotContain(JiraHarness.ApiToken);
    }

    [Fact]
    public async Task TheConnection_SurvivesARestart()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        var restarted = new JiraAuthenticationService(
            _jira.Options, _jira.Http, _jira.OAuth, _jira.Secrets, _jira.File, _jira.Time, _jira.Log.For<JiraAuthenticationService>());

        (await restarted.GetConnectionAsync(Ct)).State.Should().Be(JiraConnectionState.Connected);
    }

    [Fact]
    public async Task AConnectionFileWithoutItsSecret_AsksToReconnect()
    {
        // O jira.json veio de outra máquina num backup, e o DPAPI daqui não decifra.
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Secrets.Secrets.Clear();

        var restarted = new JiraAuthenticationService(
            _jira.Options, _jira.Http, _jira.OAuth, _jira.Secrets, _jira.File, _jira.Time, _jira.Log.For<JiraAuthenticationService>());

        var access = () => restarted.GetAccessAsync(Ct);

        (await access.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.Unauthorized);
    }

    [Fact]
    public async Task Disconnect_ForgetsTheSecretAndTheConnection()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        await _jira.Auth.DisconnectAsync(Ct);

        (await _jira.Auth.GetConnectionAsync(Ct)).State.Should().Be(JiraConnectionState.Disconnected);
        _jira.Secrets.Secrets.Should().BeEmpty();
        _jira.ConnectionFileText.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAConnection_AccessIsNotConnected()
    {
        var access = () => _jira.Auth.GetAccessAsync(Ct);

        (await access.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.NotConnected);
    }

    [Fact]
    public async Task TheDefaultProject_IsRemembered()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        var connection = await _jira.Auth.SetDefaultProjectAsync("gaeco", Ct);

        connection.DefaultProject.Should().Be("GAECO");
        (await _jira.Auth.GetAccessAsync(Ct)).DefaultProject.Should().Be("GAECO");
    }

    [Fact]
    public async Task ADefaultProjectThatIsNotAKey_IsRefused()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        var set = () => _jira.Auth.SetDefaultProjectAsync("GAECO\" OR 1=1", Ct);

        await set.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Projects_AreListedForTheDefaultProjectPicker()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/project/search", HttpStatusCode.OK,
            """{"values":[{"key":"GAECO","name":"Gaeco"},{"key":"ECO","name":"Ecossistema"}]}""");

        var projects = await _jira.Auth.ListProjectsAsync(Ct);

        projects.Select(project => project.Key).Should().Equal("GAECO", "ECO");
    }

    [Fact]
    public async Task OAuth_TheWholeRoundTrip_ConnectsTheSite()
    {
        var authorization = await _jira.Auth.BeginAuthorizationAsync(Ct);
        var query = ParseQuery(authorization.AuthorizeUrl);

        authorization.AuthorizeUrl.Host.Should().Be("auth.atlassian.com");
        query["client_id"].Should().Be("client-id-de-teste");
        query["scope"].Should().Contain("offline_access");
        query["code_challenge_method"].Should().Be("S256");
        query["redirect_uri"].Should().Be(_jira.Options.RedirectUri.ToString());

        // O "navegador" volta para o app com o código e o state certos.
        await BrowserReturnsAsync($"code=codigo-123&state={Uri.EscapeDataString(query["state"])}");
        var connection = await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        connection.State.Should().Be(JiraConnectionState.Connected);
        connection.Method.Should().Be(JiraAuthMethod.OAuth);
        connection.SiteUrl.Should().Be(JiraHarness.Site);
        connection.AccountName.Should().Be("Ana Dev");

        var exchange = _jira.Server.Requests.Single(request => request.Uri.AbsolutePath == "/oauth/token");
        exchange.Body.Should().Contain("\"code\":\"codigo-123\"").And.Contain("code_verifier");

        var access = await _jira.Auth.GetAccessAsync(Ct);
        access.ApiBase.ToString().Should().Be($"https://api.atlassian.com/ex/jira/{JiraHarness.CloudId}/");
        access.Authorization.ToString().Should().Be($"Bearer {JiraHarness.AccessToken}");
        access.SiteUrl.ToString().Should().Be(JiraHarness.Site + "/");

        _jira.Secrets.Secrets[JiraAuthenticationService.SecretName].Should().Contain(JiraHarness.RefreshToken);
        _jira.ConnectionFileText.Should().NotContain(JiraHarness.RefreshToken).And.NotContain(JiraHarness.AccessToken);
    }

    [Fact]
    public async Task OAuth_AReturnWithTheWrongState_IsIgnored()
    {
        var authorization = await _jira.Auth.BeginAuthorizationAsync(Ct);
        var state = ParseQuery(authorization.AuthorizeUrl)["state"];

        await BrowserReturnsAsync("code=codigo-de-outro&state=forjado", expect: HttpStatusCode.NotFound);
        authorization.Completion.IsCompleted.Should().BeFalse();

        await BrowserReturnsAsync($"code=codigo-123&state={Uri.EscapeDataString(state)}");
        (await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct)).IsConnected.Should().BeTrue();

        _jira.Server.Requests.Should().NotContain(request => request.Body != null && request.Body.Contains("codigo-de-outro"));
    }

    [Fact]
    public async Task OAuth_DeniedByTheUser_SaysSo()
    {
        var authorization = await _jira.Auth.BeginAuthorizationAsync(Ct);
        var state = ParseQuery(authorization.AuthorizeUrl)["state"];

        await BrowserReturnsAsync($"error=access_denied&state={Uri.EscapeDataString(state)}");

        var wait = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await wait.Should().ThrowAsync<DomainException>().WithMessage("*recusada*");
        (await _jira.Auth.GetConnectionAsync(Ct)).State.Should().Be(JiraConnectionState.Disconnected);
    }

    [Fact]
    public async Task OAuth_SeveralSites_AsksWhichOne()
    {
        _jira.Server.On("/oauth/token/accessible-resources", HttpStatusCode.OK, """
            [{"id":"a","name":"empresa","url":"https://empresa.atlassian.net","scopes":["read:jira-work"]},
             {"id":"b","name":"cliente","url":"https://cliente.atlassian.net","scopes":["read:jira-work"]},
             {"id":"c","name":"wiki","url":"https://wiki.atlassian.net","scopes":["read:confluence-content.all"]}]
            """);

        var authorization = await _jira.Auth.BeginAuthorizationAsync(Ct);
        await BrowserReturnsAsync($"code=x&state={Uri.EscapeDataString(ParseQuery(authorization.AuthorizeUrl)["state"])}");
        var connection = await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        connection.State.Should().Be(JiraConnectionState.ChoosingSite);
        connection.Sites!.Select(site => site.Name).Should().Equal("empresa", "cliente");

        var chosen = await _jira.Auth.ChooseSiteAsync("b", Ct);

        chosen.State.Should().Be(JiraConnectionState.Connected);
        chosen.SiteUrl.Should().Be("https://cliente.atlassian.net");
        (await _jira.Auth.GetAccessAsync(Ct)).ApiBase.ToString().Should().EndWith("/ex/jira/b/");
    }

    [Fact]
    public async Task OAuth_AnExpiredAccessToken_IsRenewed_AndTheRotatedRefreshTokenIsSaved()
    {
        await ConnectWithOAuthAsync();
        _jira.Server.On("/oauth/token", _ => FakeJiraServer.Json(HttpStatusCode.OK,
            """{"access_token":"access-2","refresh_token":"refresh-token-secreto-2","expires_in":3600}"""));

        _jira.Time.Advance(TimeSpan.FromHours(2));
        var access = await _jira.Auth.GetAccessAsync(Ct);

        access.Authorization.ToString().Should().Be("Bearer access-2");
        var refresh = _jira.Server.Requests.Last(request => request.Uri.AbsolutePath == "/oauth/token");
        refresh.Body.Should().Contain("\"grant_type\":\"refresh_token\"").And.Contain(JiraHarness.RefreshToken);
        _jira.Secrets.Secrets[JiraAuthenticationService.SecretName].Should().Contain("refresh-token-secreto-2");
    }

    [Fact]
    public async Task OAuth_ATokenStillValid_IsNotRenewed()
    {
        await ConnectWithOAuthAsync();
        var tokenCalls = _jira.Server.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token");

        await _jira.Auth.GetAccessAsync(Ct);
        await _jira.Auth.GetAccessAsync(Ct);

        _jira.Server.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token").Should().Be(tokenCalls);
    }

    [Fact]
    public async Task OAuth_ARevokedRefreshToken_MeansReconnect()
    {
        await ConnectWithOAuthAsync();
        _jira.Server.On("/oauth/token", HttpStatusCode.Forbidden, """{"error":"invalid_grant"}""");
        _jira.Time.Advance(TimeSpan.FromHours(2));

        var access = () => _jira.Auth.GetAccessAsync(Ct);

        (await access.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.Unauthorized);
    }

    [Fact]
    public async Task OAuth_RenewingOffline_IsUnavailable_NotUnauthorized()
    {
        await ConnectWithOAuthAsync();
        _jira.Time.Advance(TimeSpan.FromHours(2));
        _jira.Server.Offline = true;

        var access = () => _jira.Auth.GetAccessAsync(Ct);

        (await access.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.Unavailable);
    }

    [Fact]
    public async Task NoSecret_EverReachesTheLog()
    {
        await ConnectWithOAuthAsync();
        _jira.Time.Advance(TimeSpan.FromHours(2));
        await _jira.Auth.GetAccessAsync(Ct);
        await _jira.Auth.DisconnectAsync(Ct);
        await _jira.ConnectWithApiTokenAsync(Ct);

        _jira.Log.Lines.Should().NotBeEmpty();
        _jira.Log.Mentions(JiraHarness.ApiToken).Should().BeFalse();
        _jira.Log.Mentions(JiraHarness.AccessToken).Should().BeFalse();
        _jira.Log.Mentions(JiraHarness.RefreshToken).Should().BeFalse();
        _jira.Log.Mentions("client-secret-de-teste").Should().BeFalse();
        _jira.Log.Mentions("codigo-123").Should().BeFalse();
        _jira.Log.Mentions("Basic ").Should().BeFalse();
        _jira.Log.Mentions("Bearer ").Should().BeFalse();
    }

    private async Task ConnectWithOAuthAsync()
    {
        var authorization = await _jira.Auth.BeginAuthorizationAsync(Ct);
        await BrowserReturnsAsync($"code=codigo-123&state={Uri.EscapeDataString(ParseQuery(authorization.AuthorizeUrl)["state"])}");
        await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private async Task BrowserReturnsAsync(string query, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var browser = new HttpClient();
        using var response = await browser.GetAsync(new Uri(_jira.Options.RedirectUri + "?" + query), Ct);

        response.StatusCode.Should().Be(expect);
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
}
