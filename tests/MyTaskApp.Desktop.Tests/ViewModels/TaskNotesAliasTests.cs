using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O autocomplete de diretórios na anotação (ADR-026), no ViewModel: quando a
/// lista abre, o que ela mostra e como o teclado anda nela.
/// </summary>
public class TaskNotesAliasTests
{
    private static readonly DateOnly Date = new(2026, 9, 23);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid EcoId = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();

    internal static readonly IReadOnlyList<TagDirectoryRow> Directories =
    [
        Directory(EcoId, "ECO CORE", "@eco-scripts", @"C:\Projects\eco-scripts"),
        Directory(EcoId, "ECO CORE", "@ecossistema-core", @"C:\Projects\ecossistema-core"),
        Directory(EcoId, "ECO CORE", "@ecossistema-web", @"C:\Projects\ecossistema-web"),
        Directory(AppId, "MY TASK APP", "@mytaskapp", @"C:\Projetos\MyTaskApp"),
    ];

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeDirectoryProbe _probe = new();

    private static TagDirectoryRow Directory(Guid tagId, string tagName, string alias, string path) =>
        new(Guid.NewGuid(), tagId, tagName, "#22C55E", alias, path, null, null);

    internal static TaskRowViewModel Row(bool isCompleted = false) =>
        new(
            new TodayTask(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "Corrigir problema no processamento dos animais",
                TaskPriority.Normal,
                Date,
                null,
                false,
                null,
                0,
                null,
                null),
            isCompleted);

    private async Task<TaskNotesViewModel> LoadedAsync(
        IReadOnlyList<TagDirectoryRow>? directories = null,
        bool isCompleted = false)
    {
        _runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = directories ?? Directories;

        var viewModel = new TaskNotesViewModel(_runner, _probe, TestDevelopment.For(_runner), NullLogger<TaskNotesViewModel>.Instance);
        viewModel.Load(Row(isCompleted));
        await viewModel.LoadAliasesAsync(Ct);

        return viewModel;
    }

    private static void Type(TaskNotesViewModel viewModel, string text)
    {
        viewModel.Text = text;
        viewModel.Completion.UpdateCompletion(text, text.Length);
    }

    [Fact]
    public async Task TypingTheAt_ListsEveryAliasOfTheTasksTags()
    {
        var viewModel = await LoadedAsync();

        Type(viewModel, "Verificar o código no @");

        viewModel.Completion.IsCompletionOpen.Should().BeTrue();
        viewModel.Completion.Suggestions.Select(item => item.Alias)
            .Should().Equal("@eco-scripts", "@ecossistema-core", "@ecossistema-web", "@mytaskapp");
        viewModel.Completion.SelectedSuggestion.Should().BeSameAs(viewModel.Completion.Suggestions[0]);
        viewModel.Completion.Suggestions[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task TypingAfterTheAt_Filters()
    {
        var viewModel = await LoadedAsync();

        Type(viewModel, "Verificar o código no @ecos");

        viewModel.Completion.Suggestions.Select(item => item.Alias)
            .Should().Equal("@ecossistema-core", "@ecossistema-web");
        viewModel.Completion.CompletionToken.Should().Be(new MyTaskApp.Desktop.Notes.AliasToken(22, "ecos"));
    }

    [Fact]
    public async Task NothingMatches_ClosesTheList()
    {
        var viewModel = await LoadedAsync();
        Type(viewModel, "@eco");

        Type(viewModel, "@ecoxyz");

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
        viewModel.Completion.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task ATaskWithoutDirectories_NeverOpensTheList()
    {
        var viewModel = await LoadedAsync(directories: []);

        Type(viewModel, "@");

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ACompletedTask_IsReadOnly_AndLoadsNoAliases()
    {
        var viewModel = await LoadedAsync(isCompleted: true);

        viewModel.Completion.UpdateCompletion("@", 1);

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(GetTaskDirectoriesHandler));
    }

    [Fact]
    public async Task TheArrows_WalkTheList_AndWrapAround()
    {
        var viewModel = await LoadedAsync();
        Type(viewModel, "@eco");

        viewModel.Completion.MoveSelection(1);
        viewModel.Completion.SelectedSuggestion!.Alias.Should().Be("@ecossistema-core");

        viewModel.Completion.MoveSelection(-1);
        viewModel.Completion.MoveSelection(-1);
        viewModel.Completion.SelectedSuggestion!.Alias.Should().Be("@ecossistema-web");
        viewModel.Completion.Suggestions.Count(item => item.IsSelected).Should().Be(1);
    }

    [Fact]
    public async Task TypingOneMoreLetter_KeepsTheChosenItemWhenItStillMatches()
    {
        var viewModel = await LoadedAsync();
        Type(viewModel, "@eco");
        viewModel.Completion.MoveSelection(2);
        viewModel.Completion.SelectedSuggestion!.Alias.Should().Be("@ecossistema-web");

        Type(viewModel, "@ecos");

        viewModel.Completion.SelectedSuggestion!.Alias.Should().Be("@ecossistema-web");
    }

    [Fact]
    public async Task Escape_ClosesTheList_AndItStaysClosedForThatAt()
    {
        var viewModel = await LoadedAsync();
        Type(viewModel, "@eco");

        viewModel.Completion.DismissCompletion();
        Type(viewModel, "@ecos");

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();

        Type(viewModel, "@ecos e @");

        viewModel.Completion.IsCompletionOpen.Should().BeTrue();
    }

    [Fact]
    public async Task AMissingFolder_IsFlagged_AndAnExistingOneIsNot()
    {
        _probe.Existing.Add(@"C:\Projects\ecossistema-core");
        var viewModel = await LoadedAsync();

        Type(viewModel, "@ecossistema");

        viewModel.Completion.Suggestions.Single(item => item.Alias == "@ecossistema-core").IsMissing.Should().BeFalse();
        viewModel.Completion.Suggestions.Single(item => item.Alias == "@ecossistema-web").IsMissing.Should().BeTrue();
    }

    /// <summary>A anotação continua funcionando sem os atalhos se a consulta falhar.</summary>
    [Fact]
    public async Task FailingToLoadTheAliases_JustMeansNoList()
    {
        _runner.NextFailure = new InvalidOperationException("banco fora");
        var viewModel = new TaskNotesViewModel(_runner, _probe, TestDevelopment.For(_runner), NullLogger<TaskNotesViewModel>.Instance);
        viewModel.Load(Row());

        await viewModel.LoadAliasesAsync(Ct);
        Type(viewModel, "@");

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
    }
}
