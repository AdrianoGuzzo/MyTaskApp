using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Os diretórios no cartão da etiqueta, na janela "Etiquetas" (ADR-026).</summary>
public class TagDirectoriesViewModelTests
{
    private static readonly TagRow Eco = new(Guid.NewGuid(), "ECO CORE", "#22C55E", 1, 2);

    private static readonly TagDirectoryRow Core =
        new(Guid.NewGuid(), Eco.Id, Eco.Name, Eco.ColorHex, "@ecossistema-core", @"C:\Projects\ecossistema-core", "Core", null);

    private static readonly TagDirectoryRow Web =
        new(Guid.NewGuid(), Eco.Id, Eco.Name, Eco.ColorHex, "@ecossistema-web", @"C:\Projects\ecossistema-web", null, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();
    private readonly FakeDirectoryProbe _probe = new();

    private async Task<(TagsViewModel ViewModel, TagListItemViewModel Item)> LoadedAsync()
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Eco];
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[Core, Web];
        _runner.ResultsByHandler[typeof(AddTagDirectoryHandler)] = Guid.NewGuid();

        var viewModel = new TagsViewModel(_runner, _confirmation, _probe, NullLogger<TagsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);

        return (viewModel, viewModel.Tags.Single());
    }

    [Fact]
    public async Task TheCard_ShowsHowManyDirectoriesTheTagHas()
    {
        var (_, item) = await LoadedAsync();

        item.DirectoriesLabel.Should().Be("Diretórios (2)");
        item.IsExpanded.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(GetTagDirectoriesHandler));
    }

    [Fact]
    public async Task Expanding_LoadsTheDirectories_AndFlagsTheMissingFolder()
    {
        _probe.Existing.Add(Core.Path);
        var (viewModel, item) = await LoadedAsync();

        await viewModel.ToggleDirectoriesAsync(item, Ct);

        item.IsExpanded.Should().BeTrue();
        item.Directories.Select(directory => directory.Alias)
            .Should().Equal("@ecossistema-core", "@ecossistema-web");
        item.Directories[0].IsFound.Should().BeTrue();
        item.Directories[1].IsMissing.Should().BeTrue();
    }

    [Fact]
    public async Task Adding_SendsTheFormAndClearsIt()
    {
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);
        item.Alias = "@eco-scripts";
        item.Path = @"C:\Projects\eco-scripts";

        await viewModel.SaveDirectoryAsync(item, Ct);

        _runner.Invoked.Should().Contain(typeof(AddTagDirectoryHandler));
        item.Alias.Should().BeEmpty();
        item.Path.Should().BeEmpty();
        viewModel.StatusMessage.Should().Be("Diretório adicionado.");
    }

    [Fact]
    public async Task EditThenSave_Updates()
    {
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        viewModel.EditDirectory(item.Directories[0]);

        item.IsEditingDirectory.Should().BeTrue();
        item.Alias.Should().Be("@ecossistema-core");
        item.DirectoryName.Should().Be("Core");

        item.Path = @"D:\Projects\ecossistema-core";
        await viewModel.SaveDirectoryAsync(item, Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateTagDirectoryHandler));
        item.IsEditingDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedSave_KeepsTheFormAndShowsWhy()
    {
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);
        item.Alias = "@ecossistema-core";
        item.Path = @"C:\outro";
        _runner.NextFailure = new DomainException("Esta etiqueta já tem um diretório @ecossistema-core.");

        await viewModel.SaveDirectoryAsync(item, Ct);

        viewModel.ErrorMessage.Should().Contain("@ecossistema-core");
        item.Path.Should().Be(@"C:\outro");
    }

    [Fact]
    public async Task Delete_AsksFirst_AndDeclinedDeletesNothing()
    {
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        await viewModel.DeleteDirectoryAsync(item.Directories[0], Ct);

        _confirmation.LastAsked!.Message.Should().Contain("@ecossistema-core");
        _runner.Invoked.Should().NotContain(typeof(RemoveTagDirectoryHandler));
    }

    [Fact]
    public async Task Delete_Confirmed_Removes()
    {
        _confirmation.Answer = true;
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        await viewModel.DeleteDirectoryAsync(item.Directories[0], Ct);

        _runner.Invoked.Should().Contain(typeof(RemoveTagDirectoryHandler));
        viewModel.StatusMessage.Should().Be("Diretório excluído.");
    }

    [Fact]
    public async Task AReload_KeepsAnExpandedCardExpanded()
    {
        var (viewModel, item) = await LoadedAsync();
        await viewModel.ToggleDirectoriesAsync(item, Ct);

        await viewModel.LoadAsync(Ct);

        var reloaded = viewModel.Tags.Single();
        reloaded.IsExpanded.Should().BeTrue();
        reloaded.Directories.Should().HaveCount(2);
    }

    [Fact]
    public void DeletingATag_MentionsItsDirectories() =>
        TagsViewModel.DeletePrompt(Eco).Message.Should().Contain("2 diretórios");

    [Theory]
    [InlineData(@"C:\Projects\ecossistema-core", "@ecossistema-core")]
    [InlineData(@"C:\Projetos\Integração Contábil\", "@integracao-contabil")]
    [InlineData(@"C:\", "")]
    public void TheFolderPicker_SuggestsAnAliasFromTheFolderName(string path, string alias) =>
        TagDirectoryItemViewModel.SuggestAlias(path).Should().Be(alias);

    /// <summary>Uma pasta de nome comprido não pode sugerir um alias que o domínio recusa.</summary>
    [Fact]
    public void ALongFolderName_SuggestsAnAliasWithinTheLimit()
    {
        var alias = TagDirectoryItemViewModel.SuggestAlias(
            @"C:\Projetos\Sistema de Gestão Integrada de Rebanhos e Vacinação Bovina");

        alias.Length.Should().BeLessThanOrEqualTo(MyTaskApp.Domain.Tags.TagDirectory.MaxAliasLength);
        alias.Should().NotEndWith("-");
        MyTaskApp.Domain.Tags.TagDirectory.NormalizeAlias(alias).Should().Be(alias);
    }

    [Fact]
    public void UsingAFolder_KeepsAnAliasAlreadyTyped()
    {
        var item = new TagListItemViewModel(Eco) { Alias = "@meu" };

        item.UseFolder(@"C:\Projects\outro");

        item.Path.Should().Be(@"C:\Projects\outro");
        item.Alias.Should().Be("@meu");
    }
}
