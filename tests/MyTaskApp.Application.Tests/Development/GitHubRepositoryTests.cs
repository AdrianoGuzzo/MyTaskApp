using MyTaskApp.Application.Development;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>O repositório do GitHub, lido da URL do remoto (ADR-047).</summary>
public class GitHubRepositoryTests
{
    [Theory]
    [InlineData("https://github.com/acme/eco-core.git")]
    [InlineData("https://github.com/acme/eco-core")]
    [InlineData("https://github.com/acme/eco-core/")]
    [InlineData("https://user@github.com/acme/eco-core.git")]
    [InlineData("git@github.com:acme/eco-core.git")]
    [InlineData("github.com:acme/eco-core")]
    [InlineData("ssh://git@github.com/acme/eco-core.git")]
    [InlineData("  https://GitHub.com/acme/eco-core.git\n")]
    public void ReadsOwnerAndName(string url)
    {
        var repository = GitHubRepository.TryParse(url);

        repository.Should().Be(new GitHubRepository("acme", "eco-core"));
        repository!.Slug.Should().Be("github.com/acme/eco-core");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/acme/eco-core.git")]
    [InlineData("git@bitbucket.org:acme/eco-core.git")]
    [InlineData("https://dev.azure.com/acme/eco/_git/eco-core")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/eco-core/tree/main")]
    [InlineData(@"C:\Projects\bare.git")]
    [InlineData("/srv/git/eco-core.git")]
    public void AnythingElse_IsNotGitHub(string? url) =>
        GitHubRepository.TryParse(url).Should().BeNull();
}
