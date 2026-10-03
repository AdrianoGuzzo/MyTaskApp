using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Infrastructure.Jira;

namespace MyTaskApp.Infrastructure.Tests.Jira;

/// <summary>
/// A integração inteira montada contra a Atlassian falsa: transporte, OAuth,
/// autenticação e cliente — tudo de verdade, menos a rede e o cofre.
/// </summary>
internal sealed class JiraHarness : IAsyncDisposable
{
    public const string Site = "https://empresa.atlassian.net";

    public const string CloudId = "11111111-2222-3333-4444-555555555555";

    public const string ApiToken = "ATATT-token-de-api-secreto";

    public const string AccessToken = "eyJ-access-token-secreto";

    public const string RefreshToken = "refresh-token-secreto-1";

    public JiraHarness(bool withOAuth = true, int callbackPort = 0)
    {
        Options = new JiraOptions
        {
            ClientId = withOAuth ? "client-id-de-teste" : null,
            ClientSecret = withOAuth ? "client-secret-de-teste" : null,
            CallbackPort = callbackPort == 0 ? FreePort() : callbackPort,
            RequestTimeoutSeconds = 5,
        };

        Http = new JiraHttp(new HttpClient(Server), Options, Log.For<JiraHttp>());
        OAuth = new JiraOAuthClient(Http, Options, Time);
        File = new JiraConnectionFile(Path.Combine(Folder, "jira.json"), Log.For<JiraConnectionFile>());
        Auth = new JiraAuthenticationService(Options, Http, OAuth, Secrets, File, Time, Log.For<JiraAuthenticationService>());
        Client = new JiraClient(Auth, Http, Log.For<JiraClient>());

        Server
            .On("/rest/api/3/myself", HttpStatusCode.OK, """{"displayName":"Ana Dev","emailAddress":"ana@empresa.com"}""")
            .On("/oauth/token/accessible-resources", HttpStatusCode.OK, $$"""
                [{"id":"{{CloudId}}","name":"empresa","url":"{{Site}}","scopes":["read:jira-work","read:jira-user"]}]
                """)
            .On("/oauth/token", _ => FakeJiraServer.Json(HttpStatusCode.OK, $$"""
                {"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","expires_in":3600}
                """));
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "MyTaskApp.Tests", Guid.NewGuid().ToString("N"));

    public FakeJiraServer Server { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    public CapturingLog Log { get; } = new();

    public JiraOptions Options { get; }

    public JiraHttp Http { get; }

    public JiraOAuthClient OAuth { get; }

    public JiraConnectionFile File { get; }

    public JiraAuthenticationService Auth { get; }

    public JiraClient Client { get; }

    public string ConnectionFileText =>
        System.IO.File.Exists(Path.Combine(Folder, "jira.json"))
            ? System.IO.File.ReadAllText(Path.Combine(Folder, "jira.json"))
            : string.Empty;

    public Task ConnectWithApiTokenAsync(CancellationToken cancellationToken) =>
        Auth.ConnectWithApiTokenAsync("empresa", "ana@empresa.com", ApiToken, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Auth.Dispose();

        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
