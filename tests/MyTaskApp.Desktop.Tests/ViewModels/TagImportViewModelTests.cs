using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Sync.Contracts;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Importar de outro computador…" no formulário de etiqueta nova (ADR-077):
/// oferece só as que estão em outro computador, e importar faz a mesma
/// etiqueta aparecer em todos, com os diretórios esperando a pasta daqui.
/// </summary>
public sealed class TagImportViewModelTests
{
    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private static readonly TagRow Mine = new(Guid.NewGuid(), "Urgente", "#EF4444", 3, Placement: DevicePlacement.On(Work));

    private static readonly TagRow Shared = new(Guid.NewGuid(), "Financeiro", "#3B82F6", 0, Placement: DevicePlacement.Everywhere);

    private static readonly TagRow Legacy = new(Guid.NewGuid(), "Pessoal", "#A855F7", 0);

    private static readonly TagRow Backend =
        new(Guid.NewGuid(), "Backend", "#22C55E", 0, DirectoryCount: 2, Placement: DevicePlacement.On(Home));

    private static readonly TagRow Orphan =
        new(Guid.NewGuid(), "Antiga", "#F97316", 0, Placement: DevicePlacement.On(Guid.CreateVersion7()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private TagsViewModel ViewModel(DevicePlacementState? placements) =>
        new(
            _runner,
            new FakeConfirmationDialog(),
            new FakeDirectoryProbe(),
            new FakeDirectoryCommandEditor(),
            new FakeClipboardWriter(),
            new FakeShellLauncher(),
            NullLogger<TagsViewModel>.Instance,
            placements);

    [Fact]
    public async Task Load_OffersOnlyTheTagsThatAreOnAnotherComputer_WithWhereTheyAre()
    {
        _runner.Enqueue<GetTagsHandler>(
            (IReadOnlyList<TagRow>)[Shared, Legacy, Mine],
            (IReadOnlyList<TagRow>)[Orphan, Backend, Shared, Legacy, Mine]);
        var viewModel = ViewModel(Connected(("Notebook", Home)));

        await viewModel.LoadAsync(Ct);

        viewModel.ImportableTags.Select(choice => choice.Label)
            .Should().Equal("Backend — Notebook", "Antiga — um computador removido");
        viewModel.CanImport.Should().BeTrue();
        viewModel.Tags.Select(tag => tag.Row.Name).Should().Equal("Financeiro", "Pessoal", "Urgente");
    }

    [Fact]
    public async Task Load_WithoutAnotherComputer_OffersNothing_AndDoesNotAskTwice()
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Mine, Backend];
        var viewModel = ViewModel(placements: null);

        await viewModel.LoadAsync(Ct);

        viewModel.ImportableTags.Should().BeEmpty();
        viewModel.CanImport.Should().BeFalse();
        _runner.Invoked.Count(type => type == typeof(GetTagsHandler)).Should().Be(1);
    }

    [Fact]
    public async Task Load_WhenTheOtherComputersCannotBeRead_KeepsTheListAndHidesTheButton()
    {
        _runner.Enqueue<GetTagsHandler>((IReadOnlyList<TagRow>)[Mine], new InvalidOperationException("banco ocupado"));
        var viewModel = ViewModel(Connected(("Notebook", Home)));

        await viewModel.LoadAsync(Ct);

        viewModel.Tags.Should().ContainSingle();
        viewModel.CanImport.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Editing_HidesTheImport()
    {
        _runner.Enqueue<GetTagsHandler>((IReadOnlyList<TagRow>)[Mine], (IReadOnlyList<TagRow>)[Mine, Backend]);
        var viewModel = ViewModel(Connected(("Notebook", Home)));
        await viewModel.LoadAsync(Ct);

        viewModel.Edit(viewModel.Tags.Single());

        viewModel.CanImport.Should().BeFalse();

        viewModel.CancelEdit();

        viewModel.CanImport.Should().BeTrue();
    }

    [Fact]
    public async Task Import_ShowsTheSameTagOnEveryComputer_AndOpensItsDirectories()
    {
        var placements = Connected(("Notebook", Home));
        var tag = Tag.Create("Backend", "#22C55E", DateTimeOffset.UnixEpoch, DevicePlacement.On(Home));
        var imported = Backend with { Id = tag.Id, Placement = DevicePlacement.Everywhere };
        var tags = Substitute.For<ITagRepository>();
        tags.FindByIdAsync(tag.Id, Arg.Any<CancellationToken>()).Returns(tag);
        _runner.Handlers[typeof(PlaceTagHandler)] = new PlaceTagHandler(
            tags, Substitute.For<IUnitOfWork>(), placements, NullLogger<PlaceTagHandler>.Instance);
        _runner.Enqueue<GetTagsHandler>(
            (IReadOnlyList<TagRow>)[Mine],
            (IReadOnlyList<TagRow>)[Backend with { Id = tag.Id }, Mine],
            (IReadOnlyList<TagRow>)[imported, Mine],
            (IReadOnlyList<TagRow>)[imported, Mine]);
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)
        [
            new(Guid.NewGuid(), tag.Id, "Backend", "#22C55E", "@api", null, null, null,
                RepositoryUrl: "https://github.com/eco/api.git"),
            new(Guid.NewGuid(), tag.Id, "Backend", "#22C55E", "@worker", null, null, null,
                RepositoryUrl: "https://github.com/eco/worker.git"),
        ];
        var viewModel = ViewModel(placements);
        var changed = 0;
        viewModel.Changed += () => changed++;
        await viewModel.LoadAsync(Ct);

        await viewModel.ImportTagAsync(viewModel.ImportableTags.Single(), Ct);

        tag.Placement.Should().Be(DevicePlacement.Everywhere);
        changed.Should().Be(1);
        viewModel.ImportableTags.Should().BeEmpty();
        var card = viewModel.Tags.Single(item => item.Row.Id == tag.Id);
        card.IsExpanded.Should().BeTrue();
        card.Directories.Should().HaveCount(2).And.OnlyContain(directory => directory.IsUnmapped);
        viewModel.StatusMessage.Should().Be(
            "Etiqueta “Backend” importada de Notebook: agora aparece em todos os computadores."
            + " Escolha a pasta ou clone os repositórios dela.");
    }

    [Fact]
    public async Task Import_WhenItFails_SaysSo_AndKeepsTheOffer()
    {
        _runner.Enqueue<GetTagsHandler>((IReadOnlyList<TagRow>)[Mine], (IReadOnlyList<TagRow>)[Backend, Mine]);
        _runner.FailuresByHandler[typeof(PlaceTagHandler)] = new InvalidOperationException("sem banco");
        var viewModel = ViewModel(Connected(("Notebook", Home)));
        await viewModel.LoadAsync(Ct);

        await viewModel.ImportTagAsync(viewModel.ImportableTags.Single(), Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível importar a etiqueta.");
        viewModel.ImportableTags.Should().ContainSingle();
    }

    /// <summary>Na janela de verdade: o botão fica no formulário de etiqueta nova, e o menu traz uma linha por etiqueta.</summary>
    [AvaloniaFact]
    public async Task TheTagsWindow_OffersTheImportOnlyForANewTag()
    {
        _runner.Enqueue<GetTagsHandler>((IReadOnlyList<TagRow>)[Mine], (IReadOnlyList<TagRow>)[Backend, Mine]);
        var viewModel = ViewModel(Connected(("Notebook", Home)));
        await viewModel.LoadAsync(CancellationToken.None);

        var window = new TagsWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => Equals(candidate.Content, "Importar de outro computador…"));
        button.IsEffectivelyVisible.Should().BeTrue();

        var item = TagsWindow.BuildImportMenu(viewModel).Items.OfType<MenuItem>().Should().ContainSingle().Subject;
        item.Header.Should().Be("Backend — Notebook");
        item.Command.Should().BeSameAs(viewModel.ImportTagCommand);
        item.CommandParameter.Should().BeSameAs(viewModel.ImportableTags.Single());

        viewModel.Edit(viewModel.Tags.Single());
        window.UpdateLayout();

        button.IsEffectivelyVisible.Should().BeFalse();
    }

    private static DevicePlacementState Connected(params (string Nickname, Guid Id)[] others)
    {
        var directory = new SyncDeviceDirectory();
        directory.Update(others.Select(other => Device(other.Nickname, other.Id)));
        var state = new DevicePlacementState(new FixedDevice(Work), directory);
        state.Update(Work, []);
        return state;
    }

    private static DeviceInfo Device(string nickname, Guid id) =>
        new(id, nickname, "Windows", "1.0.0", DateTimeOffset.UnixEpoch, null, null, false);

    private sealed class FixedDevice(Guid id) : IDeviceIdentity
    {
        public Guid DeviceId => id;

        public string DefaultNickname => "PC";

        public void Rotate() => throw new NotSupportedException();
    }
}
