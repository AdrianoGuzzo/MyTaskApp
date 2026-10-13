using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// As etiquetas no cabeçalho da janela da tarefa (ADR-025): o mesmo seletor da
/// linha, gravando a cada marca, fora do "Salvar" do título e da anotação.
/// </summary>
public class TaskTagEditorViewModelTests
{
    private static readonly DateOnly Date = new(2026, 10, 13);

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

    private TaskTagEditorViewModel Editor(TodayTask task, bool isCompleted = false)
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Finance, Health, Urgent];

        var editor = new TaskTagEditorViewModel(_runner, NullLogger<TaskTagEditorViewModel>.Instance);
        editor.Load(new TaskRowViewModel(task, isCompleted));

        return editor;
    }

    [Fact]
    public void Loading_ShowsTheTasksTagsAsPills_InAlphabeticalOrder()
    {
        var editor = Editor(Task(Badge(Urgent), Badge(Finance)));

        editor.Tags.Selected.Select(tag => tag.Name).Should().Equal("Financeiro", "Urgente");
        editor.IsEditable.Should().BeTrue();
        editor.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Opening_ListsEveryTagAndMarksTheOnesAlreadyChosen()
    {
        var editor = Editor(Task(Badge(Urgent)));

        await editor.OpenAsync(Ct);

        editor.Tags.Options.Select(option => (option.Tag.Name, option.IsSelected))
            .Should().Equal(("Financeiro", false), ("Saúde", false), ("Urgente", true));
    }

    /// <summary>
    /// A janela fica aberta por horas. Se a linha trocou as etiquetas nesse
    /// meio-tempo, marcar sobre o retrato velho gravaria o conjunto velho de volta.
    /// </summary>
    [Fact]
    public async Task Opening_RereadsTheTasksTags_SoAChangeMadeByTheRowIsNotUndone()
    {
        var task = Task(Badge(Urgent));
        var editor = Editor(task);
        _runner.ResultsByHandler[typeof(GetTodayBoardHandler)] =
            new BoardTask(task with { Tags = [Badge(Health)] }, IsCompleted: false);

        await editor.OpenAsync(Ct);
        await editor.ToggleTagAsync(editor.Tags.Options.Single(option => option.Tag.Id == Finance.Id), Ct);

        editor.Tags.SelectedIds.Should().BeEquivalentTo([Health.Id, Finance.Id]);
        editor.Tags.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Opening_WhenTheTaskCannotBeReread_KeepsWhatTheWindowShowed()
    {
        var editor = Editor(Task(Badge(Urgent)));
        _runner.FailuresByHandler[typeof(GetTodayBoardHandler)] = new InvalidOperationException("SQLite Error 5");

        await editor.OpenAsync(Ct);

        editor.Tags.SelectedIds.Should().Equal(Urgent.Id);
        editor.Tags.Options.Should().HaveCount(3);
        editor.Tags.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Opening_WhenTheListFails_SaysSoInsideThePicker()
    {
        var editor = Editor(Task());
        _runner.FailuresByHandler[typeof(GetTagsHandler)] = new InvalidOperationException("SQLite Error 5");

        await editor.OpenAsync(Ct);

        editor.Tags.ErrorMessage.Should().Be("Não foi possível carregar as etiquetas.");
        editor.Tags.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Toggling_SavesTheWholeSetRightAway_AndTellsTheList()
    {
        var editor = Editor(Task(Badge(Urgent)));
        var changes = 0;
        editor.Changed += () => changes++;
        await editor.OpenAsync(Ct);

        var finance = editor.Tags.Options.Single(option => option.Tag.Id == Finance.Id);
        await editor.ToggleTagAsync(finance, Ct);

        _runner.LastInvoked.Should().Be<SetTaskTagsHandler>();
        editor.Tags.SelectedIds.Should().BeEquivalentTo([Urgent.Id, Finance.Id]);
        editor.Tags.Selected.Select(tag => tag.Name).Should().Equal("Financeiro", "Urgente");
        finance.IsSelected.Should().BeTrue();
        changes.Should().Be(1);
    }

    [Fact]
    public async Task ThePillsCross_RemovesTheTagWithoutOpeningThePicker()
    {
        var editor = Editor(Task(Badge(Urgent), Badge(Finance)));

        await editor.RemoveTagAsync(editor.Tags.Selected.Single(tag => tag.Id == Urgent.Id), Ct);

        _runner.LastInvoked.Should().Be<SetTaskTagsHandler>();
        editor.Tags.SelectedIds.Should().Equal(Finance.Id);
    }

    [Fact]
    public async Task ARefusedSave_PutsTheMarksBackAndExplainsInsideThePicker()
    {
        var editor = Editor(Task(Badge(Urgent)));
        var changes = 0;
        editor.Changed += () => changes++;
        await editor.OpenAsync(Ct);
        _runner.NextFailure = new DomainException("Não é possível etiquetar um checklist arquivado.");

        await editor.ToggleTagAsync(editor.Tags.Options.Single(option => option.Tag.Id == Finance.Id), Ct);

        editor.Tags.SelectedIds.Should().Equal(Urgent.Id);
        editor.Tags.ErrorMessage.Should().Be("Não é possível etiquetar um checklist arquivado.");
        changes.Should().Be(0);
    }

    [Fact]
    public async Task AnUnexpectedFailure_ShowsAPlainMessage()
    {
        var editor = Editor(Task());
        await editor.OpenAsync(Ct);
        _runner.NextFailure = new InvalidOperationException("SQLite Error 19");

        await editor.ToggleTagAsync(editor.Tags.Options[0], Ct);

        editor.Tags.ErrorMessage.Should().Be("Não foi possível salvar as etiquetas.");
        editor.Tags.SelectedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ACompletedTask_KeepsItsPills_ButNothingIsSaved()
    {
        var editor = Editor(Task(Badge(Urgent)), isCompleted: true);

        await editor.RemoveTagAsync(editor.Tags.Selected.Single(), Ct);

        editor.IsEditable.Should().BeFalse();
        editor.IsVisible.Should().BeTrue();
        editor.Tags.SelectedIds.Should().Equal(Urgent.Id);
        _runner.Invoked.Should().NotContain(typeof(SetTaskTagsHandler));
    }

    [Fact]
    public void ACompletedTaskWithoutTags_HidesTheSection()
    {
        var editor = Editor(Task(), isCompleted: true);

        editor.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task RemovingTheLastTag_KeepsTheSectionWhileEditable()
    {
        var editor = Editor(Task(Badge(Urgent)));

        await editor.RemoveTagAsync(editor.Tags.Selected.Single(), Ct);

        editor.Tags.HasTags.Should().BeFalse();
        editor.IsVisible.Should().BeTrue();
    }

    [Fact]
    public void ManageTags_AsksTheAppToOpenTheTagsWindow()
    {
        var editor = Editor(Task());
        var requested = false;
        editor.ManageRequested += () => requested = true;

        editor.ManageCommand.Execute(null);

        requested.Should().BeTrue();
    }

    [Fact]
    public void TheTaskWindow_LoadsTheTagsWithTheRow()
    {
        var editor = new TaskTagEditorViewModel(_runner, NullLogger<TaskTagEditorViewModel>.Instance);
        var notes = new TaskNotesViewModel(
            _runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(new FakeUseCaseRunner()),
            NullLogger<TaskNotesViewModel>.Instance,
            tags: editor);

        notes.Load(new TaskRowViewModel(Task(Badge(Finance)), isCompleted: false));

        notes.Tags.Should().BeSameAs(editor);
        editor.Tags.Selected.Select(tag => tag.Name).Should().Equal("Financeiro");
        notes.HasUnsavedChanges.Should().BeFalse();
    }
}
