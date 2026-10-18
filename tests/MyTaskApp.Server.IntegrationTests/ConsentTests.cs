using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O <c>/connect/authorize</c> só emite sem perguntar logo depois do login que o
/// próprio pedido começou (SEC-018, ADR-074). A sessão que sobra, por dez
/// minutos, atende qualquer navegação — até a que vem de outro site —, então
/// ali a pessoa confirma numa tela com antiforgery.
/// </summary>
public sealed class ConsentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Policy = "frame-ancestors 'none'";

    private readonly List<HttpClient> _browsers = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync()
    {
        _browsers.ForEach(browser => browser.Dispose());
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task ANavigationFromAnotherSite_WithTheSessionOpen_GetsTheConsentPage_AndNoDevice()
    {
        using var browser = _api.Browser("ana");
        await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);

        var intruder = Guid.CreateVersion7();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiFactory.AuthorizePath(intruder, "Dispositivo do terceiro", "estado-do-terceiro", ApiFactory.NewVerifier()));
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Referrer = new Uri("https://terceiro.example/");

        using var response = await browser.SendAsync(request, Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a sessão que sobrou não emite código sozinha");
        response.Headers.Location.Should().BeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain(Policy)
            .And.Contain("form-action 'self' http://127.0.0.1:47833");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        page.Should().Contain("Dispositivo do terceiro").And.Contain("ana@gmail.com").And.Contain("name=\"consent\"");
        (await DeviceExistsAsync(intruder)).Should().BeFalse();
    }

    [Fact]
    public async Task TheSameAddressOpenedAgain_AfterItsCode_AsksFirst()
    {
        using var browser = _api.Browser("ana");
        var device = Guid.CreateVersion7();
        var start = ApiFactory.AuthorizePath(device, "PC-TESTE", "estado", ApiFactory.NewVerifier());

        var first = await _api.FollowToCallbackAsync("ana", start, browser: browser);
        QueryHelpers.ParseQuery(first.Query).Should().ContainKey("code", "o login que este pedido começou sai sem pergunta");

        using var again = await browser.GetAsync(start, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.OK, "a marca do login vale uma vez");
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain("name=\"consent\"");
    }

    [Fact]
    public async Task ALoginStartedByAnotherRequest_DoesNotIssueThisOne()
    {
        using var browser = _api.Browser("ana");
        var started = ApiFactory.AuthorizePath(Guid.CreateVersion7(), "PC-TESTE", "estado-a", ApiFactory.NewVerifier());

        // Segue até o login devolver para o pedido A, e pede o B no lugar.
        var next = started;

        do
        {
            using var hop = await browser.GetAsync(next, Ct);
            next = hop.Headers.Location!.OriginalString;
        }
        while (!next.StartsWith("/connect/authorize", StringComparison.Ordinal));

        var other = Guid.CreateVersion7();
        using var response = await browser.GetAsync(ApiFactory.AuthorizePath(other, "Outro", "estado-b", ApiFactory.NewVerifier()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("name=\"consent\"");
        (await DeviceExistsAsync(other)).Should().BeFalse();
    }

    [Fact]
    public async Task Connecting_OnTheConsentPage_IssuesTheCode_AndRegistersTheDevice()
    {
        var (browser, device, verifier, page) = await ConsentPageAsync("Notebook da Ana");
        using var answer = await ApiFactory.AnswerConsentAsync(browser, page, "accept");

        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callback = answer.Headers.Location!;
        var parameters = QueryHelpers.ParseQuery(callback.Query);
        callback.GetLeftPart(UriPartial.Path).Should().Be(DesktopSession.RedirectUri);
        parameters["state"].ToString().Should().Be("estado");

        var tokens = await _api.ExchangeAsync(parameters["code"].ToString(), verifier);
        var me = await new DesktopSession(_api, device, tokens).GetAsync<MeResponse>(ApiRoutes.Me);

        me.Device!.DeviceId.Should().Be(device);
        me.Device.Nickname.Should().Be("Notebook da Ana");
    }

    [Fact]
    public async Task Connecting_WithoutTheAntiforgery_IsRefused()
    {
        var (browser, device, _, page) = await ConsentPageAsync("Notebook da Ana");
        using var answer = await ApiFactory.AnswerConsentAsync(browser, page, "accept", withAntiforgery: false);

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        answer.Headers.Location.Should().BeNull();
        (await DeviceExistsAsync(device)).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_ReturnsAccessDenied_AndNoDevice()
    {
        var (browser, device, _, page) = await ConsentPageAsync("Notebook da Ana");
        using var answer = await ApiFactory.AnswerConsentAsync(browser, page, "deny");

        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var parameters = QueryHelpers.ParseQuery(answer.Headers.Location!.Query);
        parameters["error"].ToString().Should().Be("access_denied");
        parameters.Should().NotContainKey("code");
        (await DeviceExistsAsync(device)).Should().BeFalse();
    }

    [Fact]
    public async Task TheAnswerInTheQuery_DoesNotSkipTheQuestion()
    {
        using var browser = _api.Browser("ana");
        await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);
        var device = Guid.CreateVersion7();

        using var response = await browser.GetAsync(ApiFactory.AuthorizePath(device, "Outro", "estado", ApiFactory.NewVerifier()) + "&consent=accept", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DeviceExistsAsync(device)).Should().BeFalse();
    }

    [Fact]
    public async Task PromptNone_WithoutTheLoginOfThisRequest_IsConsentRequired()
    {
        using var browser = _api.Browser("ana");
        await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);
        var device = Guid.CreateVersion7();

        using var response = await browser.GetAsync(ApiFactory.AuthorizePath(device, "Outro", "estado", ApiFactory.NewVerifier(), prompt: "none"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString().Should().Be("consent_required");
        (await DeviceExistsAsync(device)).Should().BeFalse();
    }

    [Fact]
    public async Task ThePhonesConsentPage_LetsTheFormGoToTheAppScheme()
    {
        using var browser = _api.Browser("ana");
        await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);

        using var response = await browser.GetAsync(
            ApiFactory.AuthorizePath(Guid.CreateVersion7(), "Pixel", "estado", ApiFactory.NewVerifier(), SyncSchema.MobileRedirectUri, SyncSchema.MobileClientId),
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("form-action 'self' io.github.adrianoguzzo.mytaskapp:");
    }

    /// <summary>Uma sessão aberta por outro login e um pedido novo: a tela, do dispositivo <paramref name="deviceName"/>.</summary>
    private async Task<(HttpClient Browser, Guid Device, string Verifier, string Page)> ConsentPageAsync(string deviceName)
    {
        var browser = _api.Browser("ana");
        _browsers.Add(browser);
        await _api.AuthorizeAsync("ana", Guid.CreateVersion7(), browser: browser);

        var device = Guid.CreateVersion7();
        var verifier = ApiFactory.NewVerifier();

        using var response = await browser.GetAsync(ApiFactory.AuthorizePath(device, deviceName, "estado", verifier), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (browser, device, verifier, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<bool> DeviceExistsAsync(Guid deviceId)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Devices.AnyAsync(row => row.DeviceId == deviceId, Ct);
    }
}
