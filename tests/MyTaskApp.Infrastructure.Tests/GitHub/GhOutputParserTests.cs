using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.GitHub;

namespace MyTaskApp.Infrastructure.Tests.GitHub;

/// <summary>O <c>--json</c> do <c>gh pr list</c> (ADR-047).</summary>
public class GhOutputParserTests
{
    [Fact]
    public void ReadsTheFirstPullRequest()
    {
        const string json = """
            [{"isDraft":false,"number":47,"title":"feat: PR aberta na aba Desenvolvimento","url":"https://github.com/acme/eco-core/pull/47"}]
            """;

        GhOutputParser.ParseFirstPullRequest(json).Should().Be(new PullRequestInfo(
            47,
            "feat: PR aberta na aba Desenvolvimento",
            new Uri("https://github.com/acme/eco-core/pull/47"),
            IsDraft: false));
    }

    [Fact]
    public void KeepsTheDraftFlag()
    {
        const string json = """[{"isDraft":true,"number":3,"title":"wip","url":"https://github.com/a/b/pull/3"}]""";

        GhOutputParser.ParseFirstPullRequest(json)!.IsDraft.Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""{"number":1}""")]
    public void NoPullRequest_IsNull(string json) =>
        GhOutputParser.ParseFirstPullRequest(json).Should().BeNull();

    /// <summary>O link vira clique no navegador: só https.</summary>
    [Fact]
    public void IgnoresAnUrlThatIsNotHttps()
    {
        const string json = """[{"isDraft":false,"number":1,"title":"x","url":"file:///C:/Windows/notepad.exe"}]""";

        GhOutputParser.ParseFirstPullRequest(json).Should().BeNull();
    }
}
