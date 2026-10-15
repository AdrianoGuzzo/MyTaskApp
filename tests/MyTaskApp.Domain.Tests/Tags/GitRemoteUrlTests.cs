using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Domain.Tests.Tags;

/// <summary>A URL do repositório do diretório da etiqueta (ADR-064).</summary>
public class GitRemoteUrlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://github.com/empresa/projeto.git")]
    [InlineData("http://gitlab.local/empresa/projeto")]
    [InlineData("ssh://git@github.com/empresa/projeto.git")]
    [InlineData("git://servidor/projeto.git")]
    [InlineData("file:///C:/repos/projeto.git")]
    [InlineData("git@github.com:empresa/projeto.git")]
    [InlineData("servidor:projetos/projeto.git")]
    public void TheForms_GitAccepts_AreKept(string url) =>
        GitRemoteUrl.Normalize(url).Should().Be(url);

    [Fact]
    public void Blank_IsNull_AndTheRestIsTrimmed()
    {
        GitRemoteUrl.Normalize("   ").Should().BeNull();
        GitRemoteUrl.Normalize(null).Should().BeNull();
        GitRemoteUrl.Normalize("  \"https://github.com/empresa/projeto.git\"  ")
            .Should().Be("https://github.com/empresa/projeto.git");
    }

    [Theory]
    [InlineData("https://token@github.com/empresa/projeto.git")]
    [InlineData("https://usuario:senha@github.com/empresa/projeto.git")]
    public void CredentialsInAnHttpUrl_AreDropped_BeforeTheyReachTheDatabase(string url) =>
        GitRemoteUrl.Normalize(url).Should().Be("https://github.com/empresa/projeto.git");

    [Fact]
    public void TheSshUser_IsNotACredential_AndStays() =>
        GitRemoteUrl.Normalize("ssh://git@github.com/empresa/projeto.git")
            .Should().Be("ssh://git@github.com/empresa/projeto.git");

    [Theory]
    [InlineData(@"C:\Projetos\projeto")]
    [InlineData("projeto")]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("ftp://servidor/projeto.git")]
    [InlineData("https://github.com/empresa/meu projeto.git")]
    [InlineData("https://")]
    public void WhatIsNotAGitUrl_IsRejected(string url)
    {
        var normalize = () => GitRemoteUrl.Normalize(url);

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void BeyondTheMaximumLength_IsRejected()
    {
        var normalize = () => GitRemoteUrl.Normalize("https://github.com/" + new string('a', GitRemoteUrl.MaxLength));

        normalize.Should().Throw<DomainException>().WithMessage("*2048*");
    }

    [Theory]
    [InlineData("https://github.com/empresa/projeto.git", "projeto")]
    [InlineData("https://github.com/empresa/projeto/", "projeto")]
    [InlineData("git@github.com:empresa/ecossistema-core.git", "ecossistema-core")]
    [InlineData("servidor:projeto.git", "projeto")]
    [InlineData("file:///C:/repos/projeto.git", "projeto")]
    [InlineData("https://github.com", null)]
    [InlineData("", null)]
    public void FolderName_IsWhatGitCloneWouldCreate(string url, string? expected) =>
        GitRemoteUrl.FolderName(url).Should().Be(expected);

    [Theory]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b")]
    [InlineData("https://github.com/a/b/", "https://GitHub.com/a/b.git")]
    public void TheSameRepository_WrittenAnotherWay_IsTheSame(string left, string right) =>
        GitRemoteUrl.AreSame(left, right).Should().BeTrue();

    [Fact]
    public void AnotherRepository_IsNotTheSame() =>
        GitRemoteUrl.AreSame("https://github.com/a/b.git", "https://github.com/a/c.git").Should().BeFalse();

    [Fact]
    public void TheDirectory_KeepsTheUrl_FromAddAndUpdate()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);

        var directory = tag.AddDirectory(
            "@core", @"C:\Projects\core", null, null, Now, repositoryUrl: " https://github.com/eco/core.git ");

        directory.RepositoryUrl.Should().Be("https://github.com/eco/core.git");

        tag.UpdateDirectory(directory.Id, "@core", @"C:\Projects\core", null, null, null, "git@github.com:eco/core.git");
        directory.RepositoryUrl.Should().Be("git@github.com:eco/core.git");

        tag.UpdateDirectory(directory.Id, "@core", @"C:\Projects\core", null, null, null, "  ");
        directory.RepositoryUrl.Should().BeNull();
    }

    [Fact]
    public void AnInvalidUrl_LeavesTheDirectoryAsItWas()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", null, null, Now);

        var update = () => tag.UpdateDirectory(directory.Id, "@novo", @"C:\Projects\novo", null, null, null, "projeto");

        update.Should().Throw<DomainException>();
        directory.Alias.Should().Be("@core");
        directory.Path.Should().Be(@"C:\Projects\core");
    }

    [Fact]
    public void ChangeRepositoryUrl_OnlyCountsARealChange()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory(
            "@core", @"C:\Projects\core", null, null, Now, repositoryUrl: "https://github.com/eco/core.git");

        tag.ChangeDirectoryRepositoryUrl(directory.Id, "https://github.com/eco/core").Should().BeFalse();
        directory.RepositoryUrl.Should().Be("https://github.com/eco/core.git");

        tag.ChangeDirectoryRepositoryUrl(directory.Id, null).Should().BeFalse("um remoto que sumiu não apaga a URL");
        directory.RepositoryUrl.Should().Be("https://github.com/eco/core.git");

        tag.ChangeDirectoryRepositoryUrl(directory.Id, "https://github.com/eco/core-novo.git").Should().BeTrue();
        directory.RepositoryUrl.Should().Be("https://github.com/eco/core-novo.git");
    }
}
