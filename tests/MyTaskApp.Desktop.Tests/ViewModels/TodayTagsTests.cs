using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O seletor de etiquetas da linha e as bolinhas que ele acende (ADR-025).
/// </summary>
public class TodayTagsTests
{
    private static readonly DateOnly Date = new(2026, 9, 22);

    private static readonly TagRow Urgent = new(Guid.NewGuid(), "Urgente", "#EF4444", 1);
    private static readonly TagRow Finance = new(Guid.NewGuid(), "Financeiro", "#3B82F6", 0);
    private static readonly TagRow Health = new(Guid.NewGuid(), "Saúde", "#22C55E", 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private static TagBadge Badge(TagRow tag) => new(tag.Id, tag.Name, tag.ColorHex);

    private static TodayTask Task(params TagBadge[] tags) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Pagar boleto",
            TaskPriority.Normal,
            Date,
            null,
            false,
            Tags: tags);

    private TodayViewModel ViewModel() =>
        new(
            _runner,
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private async Task<(TodayViewModel ViewModel, TaskRowViewModel Row)> OpenPickerAsync(
        params TagBadge[] current)
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] =
            (IReadOnlyList<TagRow>)[Finance, Health, Urgent];

        var viewModel = ViewModel();
        var row = new TaskRowViewModel(Task(current), isCompleted: false);

        await viewModel.OpenTagPickerAsync(row.Tags, Ct);

        return (viewModel, row);
    }

    [Fact]
    public void TheRow_ShowsOneDotPerTag_InAlphabeticalOrder()
    {
        var row = new TaskRowViewModel(Task(Badge(Urgent), Badge(Finance)), isCompleted: false);

        row.Tags.HasTags.Should().BeTrue();
        row.Tags.Dots.Select(dot => dot.Name).Should().Equal("Financeiro", "Urgente");
        row.Tags.HasOverflow.Should().BeFalse();
    }

    [Fact]
    public void ARowWithoutTags_ShowsNoDots()
    {
        var row = new TaskRowViewModel(Task(), isCompleted: false);

        row.Tags.HasTags.Should().BeFalse();
        row.Tags.Dots.Should().BeEmpty();
    }

    [Fact]
    public void ManyTags_BecomeFiveDotsAndACounterThatNamesTheRest()
    {
        var tags = Enumerable.Range(1, 7)
            .Select(index => new TagBadge(Guid.NewGuid(), $"Etiqueta {index}", "#94A3B8"))
            .ToArray();

        var row = new TaskRowViewModel(Task(tags), isCompleted: false);

        row.Tags.Dots.Should().HaveCount(TaskTagsViewModel.MaxDots);
        row.Tags.HasOverflow.Should().BeTrue();
        row.Tags.OverflowLabel.Should().Be("+2");
        row.Tags.OverflowTip.Should().Be("Etiqueta 6, Etiqueta 7");
    }

    [Fact]
    public async Task OpeningThePicker_ListsEveryTagAndMarksTheOnesAlreadyChosen()
    {
        var (_, row) = await OpenPickerAsync(Badge(Urgent));

        row.Tags.Options.Select(option => (option.Tag.Name, option.IsSelected))
            .Should().Equal(("Financeiro", false), ("Saúde", false), ("Urgente", true));
        row.Tags.HasNoTagsAtAll.Should().BeFalse();
    }

    [Fact]
    public async Task OpeningThePicker_WithNoTagsYet_SaysSo()
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[];
        var viewModel = ViewModel();
        var row = new TaskRowViewModel(Task(), isCompleted: false);

        await viewModel.OpenTagPickerAsync(row.Tags, Ct);

        row.Tags.HasNoTagsAtAll.Should().BeTrue();
    }

    [Fact]
    public async Task Search_IgnoresCaseAndAccents()
    {
        var (_, row) = await OpenPickerAsync();

        row.Tags.SearchText = "SAUDE";

        row.Tags.Options.Should().ContainSingle().Which.Tag.Name.Should().Be("Saúde");
        row.Tags.HasNoMatches.Should().BeFalse();

        row.Tags.SearchText = "xyz";

        row.Tags.Options.Should().BeEmpty();
        row.Tags.HasNoMatches.Should().BeTrue();
    }

    [Fact]
    public async Task Toggling_SavesTheWholeSetAndLightsTheDot()
    {
        var (viewModel, row) = await OpenPickerAsync(Badge(Urgent));

        var finance = row.Tags.Options.Single(option => option.Tag.Id == Finance.Id);
        await viewModel.ToggleTagAsync(finance, Ct);

        _runner.LastInvoked.Should().Be<SetTaskTagsHandler>();
        row.Tags.SelectedIds.Should().BeEquivalentTo([Urgent.Id, Finance.Id]);
        row.Tags.Dots.Select(dot => dot.Name).Should().Equal("Financeiro", "Urgente");
        finance.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task TogglingAMarkedTag_Unmarks()
    {
        var (viewModel, row) = await OpenPickerAsync(Badge(Urgent));

        await viewModel.ToggleTagAsync(
            row.Tags.Options.Single(option => option.Tag.Id == Urgent.Id), Ct);

        row.Tags.SelectedIds.Should().BeEmpty();
        row.Tags.HasTags.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedSave_PutsTheMarksBackAndExplainsInsideThePicker()
    {
        var (viewModel, row) = await OpenPickerAsync(Badge(Urgent));
        _runner.NextFailure = new DomainException("Não é possível etiquetar um checklist arquivado.");

        await viewModel.ToggleTagAsync(
            row.Tags.Options.Single(option => option.Tag.Id == Finance.Id), Ct);

        row.Tags.SelectedIds.Should().Equal(Urgent.Id);
        row.Tags.ErrorMessage.Should().Be("Não é possível etiquetar um checklist arquivado.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task AnUnexpectedFailure_ShowsAPlainMessage()
    {
        var (viewModel, row) = await OpenPickerAsync();
        _runner.NextFailure = new InvalidOperationException("SQLite Error 19");

        await viewModel.ToggleTagAsync(row.Tags.Options[0], Ct);

        row.Tags.ErrorMessage.Should().Be("Não foi possível salvar as etiquetas.");
        row.Tags.SelectedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task TheChipsCross_RemovesTheTagDirectly()
    {
        var (viewModel, row) = await OpenPickerAsync(Badge(Urgent), Badge(Finance));

        await viewModel.RemoveTagAsync(row.Tags.Selected.Single(tag => tag.Id == Urgent.Id), Ct);

        _runner.LastInvoked.Should().Be<SetTaskTagsHandler>();
        row.Tags.SelectedIds.Should().Equal(Finance.Id);
    }

    [Fact]
    public async Task ClosingAfterAChange_ReloadsTheBoard()
    {
        var (viewModel, row) = await OpenPickerAsync();
        _runner.ResultsByHandler[typeof(GetTodayBoardHandler)] =
            new TodayBoard(Date, [], [], [], [], []);
        await viewModel.ToggleTagAsync(row.Tags.Options[0], Ct);

        await viewModel.CloseTagPickerAsync(row.Tags, Ct);

        _runner.LastInvoked.Should().Be<GetTodayBoardHandler>();
        viewModel.IsPickingTags.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // A caixa de captura: escolher antes de criar
    // ------------------------------------------------------------------

    private async Task<TodayViewModel> OpenCapturePickerAsync()
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] =
            (IReadOnlyList<TagRow>)[Finance, Health, Urgent];
        _runner.ResultsByHandler[typeof(GetTodayBoardHandler)] =
            new TodayBoard(Date, [], [], [], [], []);

        var viewModel = ViewModel();
        await viewModel.OpenTagPickerAsync(viewModel.CaptureTags, Ct);

        return viewModel;
    }

    [Fact]
    public async Task InTheCaptureBox_MarkingOnlyRemembers_NothingIsSaved()
    {
        var viewModel = await OpenCapturePickerAsync();

        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[0], Ct);
        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[2], Ct);

        _runner.Invoked.Should().NotContain(typeof(SetTaskTagsHandler));
        viewModel.CaptureTags.SelectedIds.Should().BeEquivalentTo([Finance.Id, Urgent.Id]);
        viewModel.CaptureTags.Dots.Select(dot => dot.Name).Should().Equal("Financeiro", "Urgente");
        viewModel.CaptureTags.Heading.Should().Be("Etiquetas das novas tarefas");
    }

    [Fact]
    public async Task ClosingTheCapturePicker_DoesNotReloadTheBoard()
    {
        var viewModel = await OpenCapturePickerAsync();
        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[0], Ct);

        await viewModel.CloseTagPickerAsync(viewModel.CaptureTags, Ct);

        _runner.Invoked.Should().NotContain(typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task ACapture_UsesTheChosenTagsAndThenStartsClean()
    {
        var viewModel = await OpenCapturePickerAsync();
        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[2], Ct);
        viewModel.CaptureText = "pagar boleto\nenviar nota";

        await viewModel.CaptureAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(MyTaskApp.Application.Tasks.QuickCaptureHandler));
        viewModel.CaptureText.Should().BeEmpty();
        viewModel.CaptureTags.HasTags.Should().BeFalse();
        viewModel.CaptureTags.Dots.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCapture_KeepsTheTextAndTheTags()
    {
        var viewModel = await OpenCapturePickerAsync();
        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[2], Ct);
        viewModel.CaptureText = "pagar boleto";
        _runner.NextFailure = new DomainException("Uma das etiquetas não existe mais. Reabra a lista e tente de novo.");

        await viewModel.CaptureAsync(Ct);

        viewModel.ErrorMessage.Should().StartWith("Uma das etiquetas não existe mais");
        viewModel.CaptureText.Should().Be("pagar boleto");
        viewModel.CaptureTags.SelectedIds.Should().Equal(Urgent.Id);
    }

    [Fact]
    public async Task ClosingWithoutChanges_DoesNotReload()
    {
        var (viewModel, row) = await OpenPickerAsync();

        viewModel.IsPickingTags.Should().BeTrue();

        await viewModel.CloseTagPickerAsync(row.Tags, Ct);

        _runner.Invoked.Should().NotContain(typeof(GetTodayBoardHandler));
    }
}
