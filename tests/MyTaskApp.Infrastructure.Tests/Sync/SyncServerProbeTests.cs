using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Infrastructure.Tests.Jira;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// "Testar conexão" (ADR-078): cada falha com o nome certo, nenhum token no
/// pedido, e o endereço anunciado pelo servidor nunca no lugar do configurado.
/// </summary>
public sealed class SyncServerProbeTests
{
    private static readonly Uri Server = new("https://sync.exemplo.com/");

    private const string Discovery = """{"issuer":"https://sync.exemplo.com/","authorization_endpoint":"https://sync.exemplo.com/connect/authorize"}""";

    private readonly CapturingLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMyTaskAppServer_IsReachable()
    {
        var server = new FakeJiraServer()
            .On("/health", HttpStatusCode.OK, """{"status":"ok"}""")
            .On("/.well-known/openid-configuration", HttpStatusCode.OK, Discovery);

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(SyncServerProbeKind.Ok);
        result.Reachable.Should().BeTrue();
        server.Requests.Select(request => request.Uri.AbsoluteUri).Should().Equal(
            "https://sync.exemplo.com/health",
            "https://sync.exemplo.com/.well-known/openid-configuration");
        server.Requests.Should().OnlyContain(request => request.Authorization == null, "o teste vem antes de qualquer login");
    }

    [Fact]
    public async Task AServerUnderAPath_IsTestedUnderThatPath()
    {
        var server = new FakeJiraServer()
            .On("/sync/health", HttpStatusCode.OK, """{"status":"ok"}""")
            .On("/sync/.well-known/openid-configuration", HttpStatusCode.OK, """{"authorization_endpoint":"https://exemplo.com/sync/connect/authorize"}""");

        var result = await Probe(server).TestAsync(new Uri("https://exemplo.com/sync/"), Ct);

        result.Kind.Should().Be(SyncServerProbeKind.Ok);
    }

    [Fact]
    public async Task AnotherAnnouncedAddress_IsOnlyAWarning_AndIsNeverAdopted()
    {
        var server = new FakeJiraServer()
            .On("/health", HttpStatusCode.OK, """{"status":"ok"}""")
            .On("/.well-known/openid-configuration", HttpStatusCode.OK, """{"authorization_endpoint":"https://outro.exemplo.com/connect/authorize"}""");

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(SyncServerProbeKind.IssuerMismatch);
        result.Reachable.Should().BeTrue();
        result.Message.Should().Contain("outro.exemplo.com");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, SyncServerProbeKind.Dns)]
    [InlineData(HttpRequestError.ConnectionError, SyncServerProbeKind.Refused)]
    [InlineData(HttpRequestError.SecureConnectionError, SyncServerProbeKind.Tls)]
    [InlineData(HttpRequestError.Unknown, SyncServerProbeKind.Unexpected)]
    public async Task NetworkFailures_AreNamed(HttpRequestError error, SyncServerProbeKind kind)
    {
        var server = new FakeJiraServer().On("/health", _ => throw new HttpRequestException(error, "falha de rede sync.exemplo.com"));

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(kind);
        result.Reachable.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ASlowServer_TimesOut()
    {
        var server = new FakeJiraServer().On("/health", _ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return FakeJiraServer.Json(HttpStatusCode.OK, """{"status":"ok"}""");
        });

        var result = await Probe(server, TimeSpan.FromMilliseconds(100)).TestAsync(Server, Ct);

        result.Kind.Should().Be(SyncServerProbeKind.Timeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, SyncServerProbeKind.Redirect)]
    [InlineData(HttpStatusCode.Found, SyncServerProbeKind.Redirect)]
    [InlineData(HttpStatusCode.NotFound, SyncServerProbeKind.NotMyTaskApp)]
    [InlineData(HttpStatusCode.BadGateway, SyncServerProbeKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SyncServerProbeKind.ServerError)]
    [InlineData(HttpStatusCode.Unauthorized, SyncServerProbeKind.Unexpected)]
    public async Task HttpAnswers_AreNamed(HttpStatusCode status, SyncServerProbeKind kind)
    {
        var server = new FakeJiraServer().On("/health", _ =>
        {
            var response = FakeJiraServer.Json(status, "{}");

            if ((int)status is >= 300 and < 400)
            {
                response.Headers.Location = new Uri("https://outro.exemplo.com/health");
            }

            return response;
        });

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(kind);
        server.Requests.Should().ContainSingle("um redirecionamento não é seguido");
    }

    [Theory]
    [InlineData("<html>Bem-vindo ao nginx</html>")]
    [InlineData("""{"status":"down"}""")]
    [InlineData("")]
    public async Task SomethingElseAnswering_IsNotAMyTaskAppServer(string body)
    {
        var server = new FakeJiraServer().On("/health", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") });

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(SyncServerProbeKind.NotMyTaskApp);
    }

    [Fact]
    public async Task WithoutTheOAuthDiscovery_ItIsNotAMyTaskAppServer()
    {
        var server = new FakeJiraServer().On("/health", HttpStatusCode.OK, """{"status":"ok"}""");

        var result = await Probe(server).TestAsync(Server, Ct);

        result.Kind.Should().Be(SyncServerProbeKind.NotMyTaskApp);
    }

    [Fact]
    public async Task TheLog_HasOnlyTheKind()
    {
        var server = new FakeJiraServer().On("/health", _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "Host sync.exemplo.com desconhecido"));

        await Probe(server).TestAsync(Server, Ct);

        _log.Mentions("Dns").Should().BeTrue();
        _log.Mentions("sync.exemplo.com").Should().BeFalse("a mensagem da falha de rede traz o endereço, e ela não vai para o log");
    }

    private SyncServerProbe Probe(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var options = new SyncOptions();
        var http = new SyncHttp(new HttpClient(handler), options, new FakeTimeProvider(), _log.For<SyncHttp>());
        return new SyncServerProbe(http, _log.For<SyncServerProbe>()) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }
}
