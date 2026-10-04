using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.GitHub;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.GitHub;

/// <summary>
/// O GitHub CLI sem <c>gh</c> nenhum (ADR-047): o que o cliente manda executar,
/// como lê a resposta e quando pergunta de novo.
/// </summary>
public class GhCliPullRequestClientTests
{
    private const string GhExe = @"C:\Program Files\GitHub CLI\gh.exe";

    private static readonly GitHubRepository Repository = new("acme", "eco-core");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeProcessRunner _runner = new();

    private readonly FakeTimeProvider _time = new();

    private GhCliPullRequestClient Client(string? executable = GhExe) =>
        new(
            _runner,
            new GhLocator(
                (name, _) => name == "ProgramFiles" ? @"C:\Program Files" : null,
                path => path == executable,
                isWindows: true),
            _time,
            NullLogger<GhCliPullRequestClient>.Instance);

    [Fact]
    public async Task FindOpen_AsksForTheOpenPullRequestOfTheBranch()
    {
        _runner.Respond("pr", new ProcessResult(
            0,
            """[{"isDraft":false,"number":12,"title":"Corrige o login","url":"https://github.com/acme/eco-core/pull/12"}]""",
            "",
            false));

        var lookup = await Client().FindOpenAsync(Repository, "bug/GAECO-1", Ct);

        lookup.Support.Should().Be(PullRequestSupport.Ready);
        lookup.PullRequest!.Number.Should().Be(12);

        var request = _runner.Requests.Single();
        request.FileName.Should().Be(GhExe);
        request.Arguments.Should().Equal(
            "pr", "list",
            "--repo", "github.com/acme/eco-core",
            "--head", "bug/GAECO-1",
            "--state", "open",
            "--json", "number,title,url,isDraft",
            "--limit", "1");
        request.Environment!["GH_PROMPT_DISABLED"].Should().Be("1");
    }

    [Fact]
    public async Task FindOpen_NoPullRequest_IsReadyWithoutOne()
    {
        _runner.Respond("pr", new ProcessResult(0, "[]", "", false));

        var lookup = await Client().FindOpenAsync(Repository, "feature/x", Ct);

        lookup.Should().Be(new PullRequestLookup(PullRequestSupport.Ready));
    }

    /// <summary>O <c>gh</c> sai com 4 quando falta autenticação.</summary>
    [Fact]
    public async Task FindOpen_ExitFour_IsNotAuthenticated()
    {
        _runner.Respond("pr", new ProcessResult(4, "", "To get started with GitHub CLI, please run:  gh auth login", false));

        var lookup = await Client().FindOpenAsync(Repository, "feature/x", Ct);

        lookup.Support.Should().Be(PullRequestSupport.NotAuthenticated);
        lookup.NeedsCli.Should().BeTrue();
    }

    [Fact]
    public async Task FindOpen_OtherErrors_AreFailures()
    {
        _runner.Respond("pr", new ProcessResult(1, "", "GraphQL: Could not resolve to a Repository", false));

        (await Client().FindOpenAsync(Repository, "feature/x", Ct)).Support.Should().Be(PullRequestSupport.Failed);
    }

    [Fact]
    public async Task FindOpen_Timeout_IsAFailure()
    {
        _runner.Respond("pr", new ProcessResult(-1, "", "", true));

        (await Client().FindOpenAsync(Repository, "feature/x", Ct)).Support.Should().Be(PullRequestSupport.Failed);
    }

    [Fact]
    public async Task WithoutGh_IsCliMissing_AndNothingRuns()
    {
        var lookup = await Client(executable: null).FindOpenAsync(Repository, "feature/x", Ct);

        lookup.Support.Should().Be(PullRequestSupport.CliMissing);
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GhThatDoesNotStart_IsCliMissing()
    {
        _runner.StartFailure = new ProcessStartException(GhExe, new IOException("Acesso negado"));

        (await Client().FindOpenAsync(Repository, "feature/x", Ct)).Support.Should().Be(PullRequestSupport.CliMissing);
    }

    [Theory]
    [InlineData(0, PullRequestSupport.Ready)]
    [InlineData(1, PullRequestSupport.NotAuthenticated)]
    public async Task Check_AsksAuthStatus(int exitCode, PullRequestSupport expected)
    {
        _runner.Respond("auth", new ProcessResult(exitCode, "", "", false));

        (await Client().CheckAsync(Repository, Ct)).Should().Be(expected);
        _runner.Requests.Single().Arguments.Should().Equal("auth", "status", "--hostname", "github.com");
    }

    /// <summary>A lista Hoje recarrega a cada minuto: a mesma pergunta não vai ao GitHub de novo tão cedo.</summary>
    [Fact]
    public async Task Answers_AreCached_UntilTheyExpire()
    {
        _runner.Respond("pr", new ProcessResult(0, "[]", "", false));
        var client = Client();

        await client.FindOpenAsync(Repository, "feature/x", Ct);
        await client.FindOpenAsync(Repository, "feature/x", Ct);
        _runner.Requests.Should().HaveCount(1);

        await client.FindOpenAsync(Repository, "feature/y", Ct);
        _runner.Requests.Should().HaveCount(2);

        _time.Advance(GhCliPullRequestClient.CacheDuration + TimeSpan.FromSeconds(1));
        await client.FindOpenAsync(Repository, "feature/x", Ct);
        _runner.Requests.Should().HaveCount(3);
    }

    /// <summary>Um timeout não pode esconder a PR por dois minutos: a falha vence logo.</summary>
    [Fact]
    public async Task Failures_AreCachedOnlyBriefly()
    {
        _runner.Respond("pr", new ProcessResult(-1, "", "", true));
        var client = Client();

        await client.FindOpenAsync(Repository, "feature/x", Ct);
        await client.FindOpenAsync(Repository, "feature/x", Ct);
        _runner.Requests.Should().HaveCount(1, "a recarga seguinte não abre outra leva de gh presos");

        _time.Advance(GhCliPullRequestClient.FailureCacheDuration + TimeSpan.FromSeconds(1));
        _runner.Respond("pr", new ProcessResult(0, "[]", "", false));

        (await client.FindOpenAsync(Repository, "feature/x", Ct)).Support.Should().Be(PullRequestSupport.Ready);
        _runner.Requests.Should().HaveCount(2);
        GhCliPullRequestClient.FailureCacheDuration.Should().BeLessThan(GhCliPullRequestClient.CacheDuration);
    }

    [Fact]
    public async Task Reset_ForgetsTheCache()
    {
        _runner.Respond("auth", new ProcessResult(1, "", "", false));
        var client = Client();

        await client.CheckAsync(Repository, Ct);
        client.Reset();
        _runner.Respond("auth", new ProcessResult(0, "", "", false));

        (await client.CheckAsync(Repository, Ct)).Should().Be(PullRequestSupport.Ready);
        _runner.Requests.Should().HaveCount(2);
    }

    /// <summary>Quem acabou de instalar o <c>gh</c> não espera o cache vencer.</summary>
    [Fact]
    public async Task CliMissing_IsNotCached()
    {
        var missing = Client(executable: null);
        await missing.FindOpenAsync(Repository, "feature/x", Ct);
        await missing.FindOpenAsync(Repository, "feature/x", Ct);

        _runner.Requests.Should().BeEmpty();
    }

    /// <summary>Responde pelo primeiro argumento do <c>gh</c>; guarda tudo o que foi pedido.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _responses = [];

        public List<ProcessRequest> Requests { get; } = [];

        public ProcessStartException? StartFailure { get; set; }

        public void Respond(string command, ProcessResult result) => _responses[command] = result;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            return Task.FromResult(_responses.TryGetValue(request.Arguments[0], out var result)
                ? result
                : new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
