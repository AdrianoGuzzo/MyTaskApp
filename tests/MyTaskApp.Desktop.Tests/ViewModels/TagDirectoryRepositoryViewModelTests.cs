using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tags;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O repositório Git no diretório da etiqueta (ADR-064): a URL sugere onde
/// clonar, o clone termina o cadastro e a pasta escolhida traz a URL dela.
/// </summary>
public class TagDirectoryRepositoryViewModelTests
{
    private const string Url = "https://github.com/eco/ecossistema-web.git";

    private static readonly TagRow Eco = new(Guid.NewGuid(), "ECO CORE", "#22C55E", 1, 1);

    private static readonly TagDirectoryRow Core =
        new(Guid.NewGuid(), Eco.Id, Eco.Name, Eco.ColorHex, "@ecossistema-core", @"C:\Projetos\ecossistema-core", null, null,
            RepositoryUrl: "https://github.com/eco/ecossistema-core.git");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeDirectoryProbe _probe = new();

    private async Task<(TagsViewModel ViewModel, TagListItemViewModel Item)> LoadedAsync(bool gitInstalled = true)
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Eco];
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[Core];
        _runner.ResultsByHandler[typeof(AddTagDirectoryHandler)] = Guid.NewGuid();
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = gitInstalled
            ? new GitInstallation(true, "2.51.0", @"C:\Program Files\Git\cmd\git.exe")
            : GitInstallation.Missing;
        _runner.ResultsByHandler[typeof(SuggestCloneFolderHandler)] = @"C:\Projetos";
        _runner.ResultsByHandler[typeof(RefreshTagDirectoryRepositoriesHandler)] = (IReadOnlyList<Guid>)[];

        var viewModel = new TagsViewModel(
            _runner, new FakeConfirmationDialog(), _probe, new FakeDirectoryCommandEditor(), NullLogger<TagsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);

        var item = viewModel.Tags.Single();
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        return (viewModel, item);
    }

    [Fact]
    public async Task TypingTheUrl_WithTheFolderEmpty_PicksWhereToClone_AndTheAlias()
    {
        var (viewModel, item) = await LoadedAsync();

        item.RepositoryUrl = Url;

        viewModel.IsGitAvailable.Should().BeTrue();
        item.Path.Should().Be(@"C:\Projetos\ecossistema-web");
        item.Alias.Should().Be("@ecossistema-web");
        item.IsCloneOffered.Should().BeTrue("a pasta ainda não existe");
        item.CanClone.Should().BeTrue();

        // Enquanto ninguém mexeu, corrigir a URL troca a sugestão junto.
        item.RepositoryUrl = "git@github.com:eco/eco-api.git";

        item.Path.Should().Be(@"C:\Projetos\eco-api");
        item.Alias.Should().Be("@eco-api");
    }

    [Fact]
    public async Task APathTheUserWrote_IsNotReplacedByTheUrl()
    {
        var (_, item) = await LoadedAsync();
        item.Alias = "@web";
        item.Path = @"D:\Trabalho\web";

        item.RepositoryUrl = Url;

        item.Path.Should().Be(@"D:\Trabalho\web");
        item.Alias.Should().Be("@web");
    }

    [Fact]
    public async Task AnotherCloneFolder_PutsTheRepositoryInASubfolderWithItsName()
    {
        var (_, item) = await LoadedAsync();
        item.RepositoryUrl = Url;

        item.UseCloneFolder(@"E:\Clientes");

        item.Path.Should().Be(@"E:\Clientes\ecossistema-web");
        item.Alias.Should().Be("@ecossistema-web");
    }

    [Fact]
    public async Task AFolderThatExists_DoesNotOfferTheClone()
    {
        _probe.Existing.Add(@"C:\Projetos\ecossistema-web");
        var (_, item) = await LoadedAsync();

        item.RepositoryUrl = Url;

        item.PathExists.Should().BeTrue();
        item.IsCloneOffered.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutGit_TheUrlAndTheCloneAreNotOffered()
    {
        var (viewModel, item) = await LoadedAsync(gitInstalled: false);

        item.RepositoryUrl = Url;

        viewModel.IsGitAvailable.Should().BeFalse();
        item.IsGitAvailable.Should().BeFalse();
        item.Path.Should().BeEmpty("sem Git não há clone para sugerir pasta");
        item.IsCloneOffered.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(SuggestCloneFolderHandler));
        _runner.Invoked.Should().NotContain(typeof(RefreshTagDirectoryRepositoriesHandler));
    }

    [Fact]
    public async Task Clone_ThenSavesTheDirectory()
    {
        var (viewModel, item) = await LoadedAsync();
        item.RepositoryUrl = Url;
        _runner.ResultsByHandler[typeof(CloneTagRepositoryHandler)] =
            new ClonedRepository(@"C:\Projetos\ecossistema-web", Url);

        await viewModel.CloneRepositoryAsync(item, Ct);

        _runner.Invoked.Should().ContainInOrder(typeof(CloneTagRepositoryHandler), typeof(AddTagDirectoryHandler));
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().Be(@"Repositório clonado em C:\Projetos\ecossistema-web. Diretório adicionado.");
        item.IsCloning.Should().BeFalse();
        item.RepositoryUrl.Should().BeEmpty("o formulário volta para um diretório novo");
    }

    [Fact]
    public async Task ACloneThatFails_ShowsWhy_AndSavesNothing()
    {
        var (viewModel, item) = await LoadedAsync();
        item.RepositoryUrl = Url;
        _runner.FailuresByHandler[typeof(CloneTagRepositoryHandler)] =
            new RepositoryCloneException("O repositório não foi encontrado. Confira a URL e se a sua conta tem acesso a ele.");

        await viewModel.CloneRepositoryAsync(item, Ct);

        viewModel.ErrorMessage.Should().StartWith("O repositório não foi encontrado");
        _runner.Invoked.Should().NotContain(typeof(AddTagDirectoryHandler));
        item.RepositoryUrl.Should().Be(Url, "o formulário fica para corrigir");
        item.IsCloning.Should().BeFalse();
    }

    [Fact]
    public async Task ACancelledClone_SaysWhatMayHaveBeenLeft()
    {
        var (viewModel, item) = await LoadedAsync();
        item.RepositoryUrl = Url;
        _runner.FailuresByHandler[typeof(CloneTagRepositoryHandler)] = new OperationCanceledException();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await viewModel.CloneRepositoryAsync(item, cancelled.Token);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().StartWith("Clone cancelado.");
        _runner.Invoked.Should().NotContain(typeof(AddTagDirectoryHandler));
    }

    [Fact]
    public async Task ChoosingAFolderThatIsARepository_FillsTheUrlFromItsOrigin()
    {
        var (viewModel, item) = await LoadedAsync();
        _runner.ResultsByHandler[typeof(ReadRepositoryUrlHandler)] = Url;

        await viewModel.UseFolderAsync(item, @"D:\Trabalho\ecossistema-web", Ct);

        item.Path.Should().Be(@"D:\Trabalho\ecossistema-web");
        item.Alias.Should().Be("@ecossistema-web");
        item.RepositoryUrl.Should().Be(Url);
    }

    [Fact]
    public async Task Saving_AFolderThatIsARepository_UsesItsOrigin()
    {
        var (viewModel, item) = await LoadedAsync();
        var tag = Tag.Create("ECO CORE", "#22C55E", DateTimeOffset.UnixEpoch);
        var tags = Substitute.For<ITagRepository>();
        tags.FindByIdAsync(Eco.Id, Arg.Any<CancellationToken>()).Returns(tag);
        _runner.Handlers[typeof(AddTagDirectoryHandler)] = new AddTagDirectoryHandler(
            tags, Substitute.For<IUnitOfWork>(), TimeProvider.System, NullLogger<AddTagDirectoryHandler>.Instance);
        _runner.ResultsByHandler[typeof(ReadRepositoryUrlHandler)] = Url;
        item.Alias = "@web";
        item.Path = @"D:\Trabalho\web";
        item.RepositoryUrl = "https://github.com/eco/digitada-errada.git";

        await viewModel.SaveDirectoryAsync(item, Ct);

        tag.Directories.Should().ContainSingle().Which.RepositoryUrl.Should().Be(Url);
    }

    [Fact]
    public async Task EditingADirectoryFromAnotherComputer_WithItsUrl_AlreadyOffersTheClone()
    {
        var unmapped = Core with { Path = null };
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[unmapped];
        var (viewModel, item) = await LoadedAsync();
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[unmapped];
        await viewModel.ToggleDirectoriesAsync(item, Ct);
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        var directory = item.Directories.Single();
        directory.UnmappedLabel.Should().Contain("clonar");

        viewModel.EditDirectory(directory);

        item.Alias.Should().Be("@ecossistema-core", "o alias cadastrado não é trocado pela sugestão");
        item.Path.Should().Be(@"C:\Projetos\ecossistema-core");
        item.RepositoryUrl.Should().Be(Core.RepositoryUrl);
        item.IsCloneOffered.Should().BeTrue();
    }

    [Fact]
    public async Task OpeningTheList_FollowsTheOrigins_AndReloadsWhenOneChanged()
    {
        _probe.Existing.Add(Core.Path!);
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Eco];
        var (viewModel, item) = await LoadedAsync();
        _runner.Invoked.Clear();
        _runner.ResultsByHandler[typeof(RefreshTagDirectoryRepositoriesHandler)] = (IReadOnlyList<Guid>)[Core.Id];

        await viewModel.ToggleDirectoriesAsync(item, Ct);
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        _runner.Invoked.Should().Equal(
            typeof(GetTagDirectoriesHandler),
            typeof(RefreshTagDirectoryRepositoriesHandler),
            typeof(GetTagDirectoriesHandler));
        item.Directories.Single().RepositoryUrl.Should().Be(Core.RepositoryUrl);
    }
}
