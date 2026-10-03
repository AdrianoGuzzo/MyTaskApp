using MyTaskApp.Domain.External;

namespace MyTaskApp.Domain.Tests.External;

public class ExternalLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static ExternalLink Bug(string title = "Corrigir erro de sincronização") =>
        ExternalLink.Create(
            "Jira",
            "GAECO-1234",
            title,
            "https://empresa.atlassian.net/browse/GAECO-1234",
            "Bug",
            "Em andamento",
            Now);

    [Fact]
    public void Create_KeepsTheSnapshotTrimmed()
    {
        var link = ExternalLink.Create(
            " Jira ",
            " GAECO-1234 ",
            "  Corrigir erro  ",
            " https://empresa.atlassian.net/browse/GAECO-1234 ",
            " Bug ",
            "  ",
            Now);

        link.Provider.Should().Be("Jira");
        link.Id.Should().Be("GAECO-1234");
        link.Title.Should().Be("Corrigir erro");
        link.Url.Should().Be("https://empresa.atlassian.net/browse/GAECO-1234");
        link.IssueType.Should().Be("Bug");
        link.Status.Should().BeNull();
        link.SyncedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("", "GAECO-1", "Título", "https://x.atlassian.net/browse/GAECO-1")]
    [InlineData("Jira", " ", "Título", "https://x.atlassian.net/browse/GAECO-1")]
    [InlineData("Jira", "GAECO-1", "", "https://x.atlassian.net/browse/GAECO-1")]
    [InlineData("Jira", "GAECO-1", "Título", "")]
    [InlineData("Jira", "GAECO-1", "Título", "browse/GAECO-1")]
    [InlineData("Jira", "GAECO-1", "Título", "file:///C:/segredo.txt")]
    [InlineData("Jira", "GAECO-1", "Título", "javascript:alert(1)")]
    public void Create_RefusesAnIncompleteOrUnsafeSnapshot(string provider, string id, string title, string url)
    {
        var create = () => ExternalLink.Create(provider, id, title, url, null, null, Now);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_CutsAnOverlongTitleInsteadOfRefusing()
    {
        // O Jira aceita resumos maiores que o título da tarefa. Recusar seria
        // impedir o vínculo por algo que o usuário não escolheu.
        var link = Bug(new string('a', ExternalLink.MaxTitleLength + 50));

        link.Title.Should().HaveLength(ExternalLink.MaxTitleLength);
    }

    [Fact]
    public void IsSameAs_ComparesProviderAndIdIgnoringCase()
    {
        var link = Bug();

        link.IsSameAs("jira", "gaeco-1234").Should().BeTrue();
        link.IsSameAs("Jira", "GAECO-1235").Should().BeFalse();
        link.IsSameAs("GitHub", "GAECO-1234").Should().BeFalse();
    }
}
