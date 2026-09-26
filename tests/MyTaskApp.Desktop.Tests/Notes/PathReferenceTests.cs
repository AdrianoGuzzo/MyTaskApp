using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.Tests.Notes;

/// <summary>
/// A aritmética do <c>@</c> no texto do agente (ADR-039): o token com barra, a
/// chave de cada ambiente e a busca por trecho nos arquivos.
/// </summary>
public class PathReferenceTests
{
    private static readonly PathIndex Index = PathIndex.Build(
    [
        "README.md",
        "docs/ARCHITECTURE.md",
        "src/MyTaskApp.Desktop/ViewModels/TaskDevelopmentViewModel.cs",
        "src/MyTaskApp.Desktop/ViewModels/TodayViewModel.cs",
        "src/MyTaskApp.Desktop/Views/TodayView.axaml",
        "src/MyTaskApp.Desktop/Views/TaskNotesWindow.axaml",
        "src/MyTaskApp.Domain/Tasks/TaskItem.cs",
    ]);

    private static AliasToken? Find(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return PathReference.FindToken(textWithCaret.Remove(caret, 1), caret);
    }

    private static IReadOnlyList<string> Search(string rest, int limit = 10) =>
        [.. Index.Search(rest, limit).Select(match => match.Entry.Path)];

    [Theory]
    [InlineData("olhe @|", 5, "")]
    [InlineData("olhe @MyTaskApp/|", 5, "MyTaskApp/")]
    [InlineData("olhe @MyTaskApp/src/tod|", 5, "MyTaskApp/src/tod")]
    [InlineData(@"olhe @MyTaskApp\src|", 5, @"MyTaskApp\src")]
    public void TheTokenGoesThroughSlashes(string text, int start, string query) =>
        Find(text).Should().Be(new AliasToken(start, query));

    [Theory]
    [InlineData("fulano@empresa|")]
    [InlineData(@"C:\Projetos\x|")]
    [InlineData("@eco core|")]
    public void AnEmailAPathOrASpace_OpenNothing(string text) =>
        Find(text).Should().BeNull();

    [Theory]
    [InlineData("", null, "")]
    [InlineData("tod", null, "tod")]
    [InlineData("MyTaskApp/", "MyTaskApp", "")]
    [InlineData("MyTaskApp/src/tod", "MyTaskApp", "src/tod")]
    [InlineData(@"MyTaskApp\src\tod", "MyTaskApp", "src/tod")]
    public void TheQuery_IsTheEnvironmentAndTheRest(string query, string? key, string rest) =>
        PathReference.Parse(query).Should().Be(new ReferenceQuery(key, rest));

    [Theory]
    [InlineData(@"C:\Projetos\MyTaskApp", "MyTaskApp")]
    [InlineData(@"C:\Projetos\MyTaskApp\", "MyTaskApp")]
    [InlineData(@"C:\Projetos\My App (novo)", "My-App--novo")]
    [InlineData(@"C:\Projetos\ação", "a--o")]
    [InlineData("/home/eu/api", "api")]
    public void TheKey_IsTheRepositoryFolder_WithTokenCharsOnly(string repository, string key) =>
        PathReference.KeyFor(repository).Should().Be(key);

    [Fact]
    public void TwoRepositoriesWithTheSameFolder_GetDistinctKeys() =>
        PathReference.KeysFor([@"C:\a\api", @"D:\b\API", @"C:\web"]).Should().Equal("api", "API-2", "web");

    [Fact]
    public void TheIndex_HasTheFoldersTheFilesMake()
    {
        Index.IsDirectory("src/MyTaskApp.Desktop/Views").Should().BeTrue();
        Index.IsDirectory("src/MyTaskApp.Desktop/Views/").Should().BeTrue();
        Index.IsDirectory("src/Nada").Should().BeFalse();
        Index.Entries.Should().Contain(entry => entry.Path == "docs" && entry.IsDirectory);
    }

    [Fact]
    public void NothingAfterTheSlash_ListsTheRoot_FoldersFirst() =>
        Search("").Should().Equal("docs", "src", "README.md");

    [Fact]
    public void AFolderAndASlash_ListsWhatIsInsideIt() =>
        Search("src/MyTaskApp.Desktop/Views/").Should().Equal(
            "src/MyTaskApp.Desktop/Views/TaskNotesWindow.axaml",
            "src/MyTaskApp.Desktop/Views/TodayView.axaml");

    [Fact]
    public void TheFolderNameIsCaseInsensitive() =>
        Search("SRC/myTaskApp.desktop/views/").Should().HaveCount(2);

    [Fact]
    public void Initials_FindTheFile_CtrlPStyle() =>
        Search("tdvm").Should().StartWith("src/MyTaskApp.Desktop/ViewModels/TaskDevelopmentViewModel.cs");

    [Fact]
    public void AFileNameStart_BeatsAMatchSpreadOverThePath() =>
        Search("today").Should().StartWith(
            "src/MyTaskApp.Desktop/Views/TodayView.axaml",
            "src/MyTaskApp.Desktop/ViewModels/TodayViewModel.cs");

    [Fact]
    public void TheExactName_ComesFirst() =>
        Search("TaskItem").Should().StartWith("src/MyTaskApp.Domain/Tasks/TaskItem.cs");

    [Fact]
    public void LettersOutOfOrder_DoNotMatch() =>
        Search("xyz").Should().BeEmpty();

    [Fact]
    public void AKnownFolder_ScopesTheSearchToIt()
    {
        var found = Search("src/MyTaskApp.Desktop/Views/tod");

        found.Should().StartWith("src/MyTaskApp.Desktop/Views/TodayView.axaml");
        found.Should().OnlyContain(path => path.StartsWith("src/MyTaskApp.Desktop/Views/", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownFolder_SearchesTheWholePath() =>
        Search("views/tod").Should().StartWith("src/MyTaskApp.Desktop/Views/TodayView.axaml");

    [Fact]
    public void FoldersAreFoundToo() =>
        Search("viewmod").Should().Contain("src/MyTaskApp.Desktop/ViewModels");

    [Fact]
    public void TheLimit_IsRespected() =>
        Search("a", limit: 3).Should().HaveCount(3);

    [Fact]
    public void TheScore_PrefersWordStartsAndRuns()
    {
        PathReference.Score("TaskDevelopmentViewModel.cs", "tdv")
            .Should().BeGreaterThan(PathReference.Score("attendevious.cs", "tdv")!.Value);
        PathReference.Score("feature/criar-referencia", "cref").Should().NotBeNull();
        PathReference.Score("abc", "").Should().Be(0);
    }
}
