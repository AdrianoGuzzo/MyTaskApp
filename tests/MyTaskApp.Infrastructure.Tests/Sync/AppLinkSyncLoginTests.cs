using System.Net;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O login do celular (ADR-067): o mesmo serviço do desktop, com o cliente
/// <c>mytaskapp-mobile</c> e a volta pelo esquema do app, entregue pela casca
/// com <see cref="AppLinkAuthorizationRedirect.Deliver"/>.
/// </summary>
public sealed class AppLinkSyncLoginTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeJiraServer _server = new();

    private readonly InMemorySecretStore _secrets = new();

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeDeviceIdentity _device = new() { DefaultNickname = "Pixel 8" };

    private readonly AppLinkAuthorizationRedirect _redirect = new(SyncSchema.MobileClientId, new Uri(SyncSchema.MobileRedirectUri));

    private readonly SyncAuthenticationService _auth;

    public AppLinkSyncLoginTests()
    {
        var options = new SyncOptions { ApiBaseUrl = "https://sync.exemplo.com" };
        var log = new CapturingLog();
        var http = new SyncHttp(new HttpClient(_server), options, _clock, log.For<SyncHttp>());
        _auth = new SyncAuthenticationService(options, http, _secrets, _device, _clock, log.For<SyncAuthenticationService>(), _redirect);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _auth.Dispose();

    [Fact]
    public async Task TheWholeLogin_UsesThePhoneClientAndTheAppScheme_Everywhere()
    {
        _server.On(ApiRoutes.Token, HttpStatusCode.OK, """{"access_token":"access-login","refresh_token":"refresh-login","expires_in":1}""");

        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var query = Form(authorization.AuthorizeUrl.Query.TrimStart('?'));

        query["client_id"].Should().Be(SyncSchema.MobileClientId);
        query["redirect_uri"].Should().Be(SyncSchema.MobileRedirectUri);
        query["code_challenge_method"].Should().Be("S256");
        query[SyncSchema.DeviceNameParameter].Should().Be("Pixel 8");

        _redirect.Deliver(new Dictionary<string, string> { ["state"] = query["state"], ["code"] = "codigo-do-app" });
        await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var exchange = Form(_server.Requests.Single(request => request.Uri.AbsolutePath == ApiRoutes.Token).Body!);
        exchange["client_id"].Should().Be(SyncSchema.MobileClientId);
        exchange["redirect_uri"].Should().Be(SyncSchema.MobileRedirectUri);
        exchange["code"].Should().Be("codigo-do-app");
        _secrets.Secrets[SyncOptions.SecretName].Should().Be("https://sync.exemplo.com/ refresh-login", "o refresh fica amarrado ao servidor que o emitiu (ADR-078)");

        // Vencido o access, a renovação também é do cliente do celular.
        _clock.Advance(TimeSpan.FromSeconds(5));
        _server.On(ApiRoutes.Token, HttpStatusCode.OK, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":900}""");
        (await _auth.GetAccessTokenAsync(Ct)).Should().Be("access-2");
        Form(_server.Requests.Last(request => request.Uri.AbsolutePath == ApiRoutes.Token).Body!)["client_id"]
            .Should().Be(SyncSchema.MobileClientId);

        _server.On(ApiRoutes.Revocation, HttpStatusCode.OK, "");
        await _auth.SignOutAsync(Ct);
        _server.Requests.Where(request => request.Uri.AbsolutePath == ApiRoutes.Revocation)
            .Select(request => Form(request.Body!)["client_id"])
            .Should().HaveCount(2).And.OnlyContain(clientId => clientId == SyncSchema.MobileClientId);
    }

    [Fact]
    public async Task ARevokedPhone_IsToldAsSuch()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var state = Form(authorization.AuthorizeUrl.Query.TrimStart('?'))["state"];

        _redirect.Deliver(new Dictionary<string, string> { ["state"] = state, ["error"] = "device_revoked" });

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await completion.Should().ThrowAsync<SyncApiException>()).Which.Kind.Should().Be(SyncFailureKind.DeviceRevoked);
    }

    [Fact]
    public async Task ThePhoneOverTheDeviceCap_IsToldToRevokeAnOldOne()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var state = Form(authorization.AuthorizeUrl.Query.TrimStart('?'))["state"];

        _redirect.Deliver(new Dictionary<string, string> { ["state"] = state, ["error"] = SyncErrorCodes.DeviceLimitReached });

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await completion.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("revogue");
    }

    [Fact]
    public async Task ADeniedLogin_SaysSo_AndStoresNothing()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);
        var state = Form(authorization.AuthorizeUrl.Query.TrimStart('?'))["state"];

        _redirect.Deliver(new Dictionary<string, string> { ["state"] = state, ["error"] = "access_denied" });

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await completion.Should().ThrowAsync<DomainException>()).Which.Message.Should().Be("O login foi recusado.");
        _secrets.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task AReturnWithAnotherState_IsNotThisLogin()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);

        _redirect.Deliver(new Dictionary<string, string> { ["state"] = "de-outro-login", ["code"] = "codigo-enfiado" });

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await completion.Should().ThrowAsync<DomainException>();
        _server.Requests.Should().NotContain(request => request.Uri.AbsolutePath == ApiRoutes.Token, "um código de outro login nunca é trocado");
    }

    [Fact]
    public async Task ClosingTheBrowser_CancelsTheLogin()
    {
        var authorization = await _auth.BeginAuthorizationAsync(Ct);

        _redirect.Abandon();

        var completion = () => authorization.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await completion.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("cancelado");
    }

    [Fact]
    public async Task ASecondLogin_ReplacesTheFirst()
    {
        var first = await _auth.BeginAuthorizationAsync(Ct);
        var second = await _auth.BeginAuthorizationAsync(Ct);
        _server.On(ApiRoutes.Token, HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r","expires_in":900}""");

        var replaced = () => first.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await replaced.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("substituído");

        _redirect.Deliver(new Dictionary<string, string>
        {
            ["state"] = Form(second.AuthorizeUrl.Query.TrimStart('?'))["state"],
            ["code"] = "codigo",
        });
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _secrets.Secrets[SyncOptions.SecretName].Should().Be("https://sync.exemplo.com/ r");
    }

    [Fact]
    public void AReturnWithNobodyWaiting_IsIgnored()
    {
        var deliver = () => _redirect.Deliver(new Dictionary<string, string> { ["state"] = "x", ["code"] = "y" });
        var abandon = () => _redirect.Abandon();

        deliver.Should().NotThrow();
        abandon.Should().NotThrow();
    }

    [Fact]
    public async Task WithoutARedirect_TheDesktopLoopbackIsTheDefault()
    {
        var options = new SyncOptions { ApiBaseUrl = "https://sync.exemplo.com", CallbackPort = 0 };
        var log = new CapturingLog();
        using var desktop = new SyncAuthenticationService(
            options,
            new SyncHttp(new HttpClient(_server), options, _clock, log.For<SyncHttp>()),
            _secrets,
            _device,
            _clock,
            log.For<SyncAuthenticationService>());

        var authorization = await desktop.BeginAuthorizationAsync(Ct);
        var query = Form(authorization.AuthorizeUrl.Query.TrimStart('?'));

        query["client_id"].Should().Be(SyncSchema.DesktopClientId);
        query["redirect_uri"].Should().StartWith("http://127.0.0.1:");
    }

    private static Dictionary<string, string> Form(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
}
