using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O <c>@</c> no texto do agente, fora da janela (ADR-039): quais ambientes a
/// lista mostra, o que aceitar insere e o que o Tab faz numa pasta.
/// </summary>
public class ReferenceCompletionViewModelTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly TaskDevelopmentView App = TestDevelopment.View(
        TaskId,
        TaskDevelopmentStatus.Ready,
        repository: @"C:\Projetos\MyTaskApp",
        worktree: @"C:\Projetos\MyTaskApp-feature-x");

    private static readonly TaskDevelopmentView Api = TestDevelopment.View(
        TaskId,
        TaskDevelopmentStatus.Ready,
        repository: @"C:\Projetos\MyTaskApi",
        worktree: @"C:\Projetos\MyTaskApi-feature-x");

    private static readonly TaskDevelopmentView Broken = TestDevelopment.View(
        TaskId,
        TaskDevelopmentStatus.Error,
        repository: @"C:\Projetos\Quebrado");

    private readonly FakeUseCaseRunner _runner = new();

    public ReferenceCompletionViewModelTests()
    {
        _runner.ResultsByHandler[typeof(ListEnvironmentFilesHandler)] = new EnvironmentFiles(
        [
            "README.md",
            "src/Views/TodayView.axaml",
            "src/ViewModels/TodayViewModel.cs",
        ]);
    }

    private ReferenceCompletionViewModel Loaded(Guid? current = null)
    {
        var references = new ReferenceCompletionViewModel(_runner, NullLogger.Instance);
        references.Load(TaskId, isReadOnly: false);
        references.SetEnvironments([App, Api, Broken], current ?? App.Id);
        return references;
    }

    private static IAliasCompletionSource Source(ReferenceCompletionViewModel references) => references;

    private static void Type(ReferenceCompletionViewModel references, string text) =>
        references.UpdateCompletion(text, text.Length);

    private static IEnumerable<string> Titles(ReferenceCompletionViewModel references) =>
        references.Suggestions.Select(item => item.Title);

    [Fact]
    public void TheAt_ListsTheReadyEnvironmentsOfTheTask()
    {
        var references = Loaded();

        Type(references, "olhe @");

        references.IsCompletionOpen.Should().BeTrue();
        Titles(references).Should().Equal("@MyTaskApp", "@MyTaskApi");
        references.Suggestions[0].Badge.Should().Be("este ambiente");
        references.Suggestions[1].Badge.Should().BeNull();
    }

    [Fact]
    public void AcceptingAnEnvironment_InsertsItsWorktreePath()
    {
        var references = Loaded();
        Type(references, "olhe @api");

        Source(references).ReplacementFor(references.SelectedSuggestion)
            .Should().Be(@"C:\Projetos\MyTaskApi-feature-x");
    }

    [Fact]
    public void TabOnAnEnvironment_GoesInsideIt()
    {
        var references = Loaded();
        Type(references, "olhe @MyTaskApp");

        Source(references).ContinuationFor(references.SelectedSuggestion).Should().Be("@MyTaskApp/");
    }

    [Fact]
    public void InsideAnEnvironment_TheRootIsListed_AndTheFilesAreAskedOnce()
    {
        var references = Loaded();

        Type(references, "@MyTaskApp/");
        Type(references, "@MyTaskApp/s");
        Type(references, "@MyTaskApp/");

        Titles(references).Should().Equal("src/", "README.md");
        _runner.Invoked.Count(type => type == typeof(ListEnvironmentFilesHandler))
            .Should().Be(2, "um pedido por ambiente pronto, e só no primeiro @");
    }

    [Fact]
    public void AFolder_GoesInOnTab_AndIsInsertedOnEnter()
    {
        var references = Loaded();
        Type(references, "@MyTaskApp/");

        var folder = references.Suggestions.Single(item => item.Title == "src/");

        Source(references).ContinuationFor(folder).Should().Be("@MyTaskApp/src/");
        Source(references).ReplacementFor(folder).Should().Be(@"C:\Projetos\MyTaskApp-feature-x\src");
    }

    [Fact]
    public void AFile_IsInsertedWithTheFullPath_AndTabAcceptsIt()
    {
        var references = Loaded();
        Type(references, "@MyTaskApp/tvm");

        var file = references.SelectedSuggestion!;

        file.Title.Should().Be("TodayViewModel.cs");
        file.Detail.Should().Be("src/ViewModels/");
        Source(references).ReplacementFor(file).Should().Be(@"C:\Projetos\MyTaskApp-feature-x\src\ViewModels\TodayViewModel.cs");
        Source(references).ContinuationFor(file).Should().BeNull();
    }

    [Fact]
    public void WithoutAnEnvironment_TwoLetters_SearchTheFilesOfAll_WithTheirKey()
    {
        var references = Loaded();

        Type(references, "@todayview");

        references.Suggestions.Should().Contain(item =>
            item.Title == "TodayView.axaml" && item.Badge == "@MyTaskApi"
            && item.FullPath == @"C:\Projetos\MyTaskApi-feature-x\src\Views\TodayView.axaml");
        references.Suggestions.Should().Contain(item => item.Title == "TodayView.axaml" && item.Badge == "@MyTaskApp");
    }

    [Fact]
    public void OneLetter_IsOnlyForEnvironments()
    {
        var references = Loaded();

        Type(references, "@r");

        references.Suggestions.Should().NotContain(item => item.Title == "README.md");
    }

    [Fact]
    public void NothingMatching_ClosesTheList_SoEnterBreaksTheLine()
    {
        var references = Loaded();

        Type(references, "@MyTaskApp/zzz");

        references.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void AFailedList_ClosesInsteadOfLoadingForever()
    {
        _runner.FailuresByHandler[typeof(ListEnvironmentFilesHandler)] = new IOException("git sumiu");
        var references = Loaded();

        Type(references, "@MyTaskApp/");

        references.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void Escape_DoesNotReopenOnTheSameAt()
    {
        var references = Loaded();
        Type(references, "@My");

        references.DismissCompletion();
        Type(references, "@MyT");

        references.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void AReadOnlyTask_NeverOpens()
    {
        var references = new ReferenceCompletionViewModel(_runner, NullLogger.Instance);
        references.Load(TaskId, isReadOnly: true);
        references.SetEnvironments([App], App.Id);

        Type(references, "@");

        references.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void AnEmailAt_IsNotAReference()
    {
        var references = Loaded();

        Type(references, "fulano@MyTaskApp");

        references.IsCompletionOpen.Should().BeFalse();
    }
}
