using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.Tags;

/// <summary>O repositório Git do diretório: a URL acompanha a pasta, e o clone (ADR-064).</summary>
public class TagDirectoryRepositoryHandlerTests
{
    private const string Url = "https://github.com/eco/ecossistema-core.git";

    private const string Target = @"C:\Projects\novo";

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _directories = new();
    private readonly FakeTagRepository _tags = new();

    private RepositoryUrlReader Reader() => new(_git, _directories, NullLogger<RepositoryUrlReader>.Instance);

    private CloneTagRepositoryHandler Clone() =>
        new(_git, _directories, Reader(), NullLogger<CloneTagRepositoryHandler>.Instance);

    private RefreshTagDirectoryRepositoriesHandler Refresh() =>
        new(_tags, _tags, _git, Reader(), NullLogger<RefreshTagDirectoryRepositoriesHandler>.Instance);

    [Fact]
    public async Task Read_GivesTheOriginOfTheFolder_WithoutCredentials()
    {
        _git.RemoteUrls[$"{FakeGitClient.Repository}|origin"] = "https://token@github.com/eco/ecossistema-core.git";

        var url = await new ReadRepositoryUrlHandler(Reader()).HandleAsync(new ReadRepositoryUrl(FakeGitClient.Repository), Ct);

        url.Should().Be(Url);
    }

    [Theory]
    [InlineData(@"C:\Projects\nao-existe")]
    [InlineData("relativo")]
    [InlineData("")]
    public async Task Read_WithoutAFolderToAsk_IsNull_AndDoesNotRunGit(string path)
    {
        var url = await Reader().ReadAsync(path, Ct);

        url.Should().BeNull();
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_WhenGitFails_IsNull()
    {
        _git.Failures[nameof(IGitClient.GetRemoteUrlAsync)] = FakeGitClient.Failed("git remote get-url origin", "Git não encontrado.", -1);

        var url = await Reader().ReadAsync(FakeGitClient.Repository, Ct);

        url.Should().BeNull();
    }

    [Fact]
    public async Task Clone_RunsGitWithTheNormalizedUrlAndPath()
    {
        var cloned = await Clone().HandleAsync(new CloneTagRepository($" {Url} ", Target + "\\"), Ct);

        cloned.Should().Be(new ClonedRepository(Target, Url));
        _git.Clones.Should().Equal($"{Url} → {Target}");
    }

    [Fact]
    public async Task Clone_IntoAFolderThatIsAlreadyACloneOfTheSameRepository_DoesNotDownloadAgain()
    {
        _git.RemoteUrls[$"{FakeGitClient.Repository}|origin"] = "https://github.com/eco/ecossistema-core";

        var cloned = await Clone().HandleAsync(new CloneTagRepository(Url, FakeGitClient.Repository), Ct);

        cloned.AlreadyCloned.Should().BeTrue();
        _git.Clones.Should().BeEmpty();
    }

    [Fact]
    public async Task Clone_WithoutGit_SaysSo()
    {
        _git.Installation = GitInstallation.Missing;

        var clone = () => Clone().HandleAsync(new CloneTagRepository(Url, Target), Ct);

        await clone.Should().ThrowAsync<RepositoryCloneException>().WithMessage("*Git não foi encontrado*");
        _git.Clones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", @"C:\Projects\novo")]
    [InlineData("https://github.com/eco/core.git", "relativo")]
    public async Task Clone_WithAnInvalidForm_IsRejectedBeforeGit(string url, string path)
    {
        var clone = () => Clone().HandleAsync(new CloneTagRepository(url, path), Ct);

        await clone.Should().ThrowAsync<DomainException>();
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Clone_Refused_CarriesTheTranslatedMessageAndTheCommand()
    {
        _git.CloneFailure = FakeGitClient.Failed(
            "git clone",
            "remote: Repository not found.\nfatal: repository 'https://github.com/eco/x.git/' not found");

        var clone = () => Clone().HandleAsync(new CloneTagRepository(Url, Target), Ct);

        var failure = (await clone.Should().ThrowAsync<RepositoryCloneException>()).Which;
        failure.Message.Should().Contain("não foi encontrado");
        failure.Command.Should().BeSameAs(_git.CloneFailure);
    }

    [Theory]
    [InlineData("fatal: destination path 'novo' already exists and is not an empty directory.", "já existe e não está vazia")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "não conseguiu entrar")]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "não conseguiu entrar")]
    [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x", "alcançar o servidor")]
    [InlineData("fatal: algo inesperado", "fatal: algo inesperado")]
    public void FailureMessage_TranslatesTheCommonRefusals(string error, string expected) =>
        CloneTagRepositoryHandler.FailureMessage(FakeGitClient.Failed("git clone", error), Target)
            .Should().Contain(expected);

    [Fact]
    public void FailureMessage_OnTimeout_SaysItWasInterrupted() =>
        CloneTagRepositoryHandler.FailureMessage(new GitCommandResult("git clone", -1, "", "", TimedOut: true), Target)
            .Should().Contain("interrompido");

    [Fact]
    public async Task Refresh_FollowsTheOriginOfEachFolder_AndSavesOnce()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var core = tag.AddDirectory("@core", FakeGitClient.Repository, null, null, Now, repositoryUrl: "https://github.com/eco/velho.git");
        var gone = tag.AddDirectory("@web", @"C:\Projects\web", null, null, Now, repositoryUrl: "https://github.com/eco/web.git");
        _tags.Seed(tag);
        _git.RemoteUrls[$"{FakeGitClient.Repository}|origin"] = Url;

        var changed = await Refresh().HandleAsync(new RefreshTagDirectoryRepositories(tag.Id), Ct);

        changed.Should().Equal(core.Id);
        core.RepositoryUrl.Should().Be(Url);
        gone.RepositoryUrl.Should().Be("https://github.com/eco/web.git", "a pasta que não existe não tem o que dizer");
        _tags.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_WithNothingNew_DoesNotSave()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        tag.AddDirectory("@core", FakeGitClient.Repository, null, null, Now, repositoryUrl: Url);
        _tags.Seed(tag);
        _git.RemoteUrls[$"{FakeGitClient.Repository}|origin"] = "https://github.com/eco/ecossistema-core";

        var changed = await Refresh().HandleAsync(new RefreshTagDirectoryRepositories(tag.Id), Ct);

        changed.Should().BeEmpty();
        _tags.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_WithoutGit_DoesNotAskAnyFolder()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        tag.AddDirectory("@core", FakeGitClient.Repository, null, null, Now);
        _tags.Seed(tag);
        _git.Installation = GitInstallation.Missing;

        var changed = await Refresh().HandleAsync(new RefreshTagDirectoryRepositories(tag.Id), Ct);

        changed.Should().BeEmpty();
        _git.Calls.Should().Equal("--version");
    }

    [Fact]
    public void SuggestCloneFolder_IsTheMostCommonParent() =>
        SuggestCloneFolderHandler.Suggest(
            [@"D:\Outros\a", @"C:\Projetos\core", @"C:\Projetos\web\", @"c:\projetos\api"])
            .Should().Be(@"C:\Projetos");

    [Fact]
    public async Task SuggestCloneFolder_WithoutDirectories_IsTheUserFolder()
    {
        var folder = await new SuggestCloneFolderHandler(new FakeTagQuery()).HandleAsync(new SuggestCloneFolder(), Ct);

        folder.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
