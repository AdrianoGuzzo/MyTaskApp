using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O <c>{</c> que autocompleta os apelidos de banco e as variáveis (ADR-060):
/// a aritmética, a fonte, a lista pós-Worktree e a janela de parâmetros.
/// </summary>
public class PlaceholderCompletionTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly SavedDatabaseRow Alias =
        new(Guid.CreateVersion7(), "lock_eco_core_1010", Guid.CreateVersion7(), "eco_core_1010", null, At);

    // --- A aritmética -------------------------------------------------------------

    [Theory]
    [InlineData("configura {lo", 13, 10, "lo")]
    [InlineData("{", 1, 0, "")]
    [InlineData("echo x{database.h", 17, 6, "database.h")]
    public void TheBrace_OpensAnywhereInTheLine(string text, int caret, int start, string query) =>
        AliasCompletion.FindToken(text, caret, PlaceholderCompletionViewModel.IsNameChar, '{')
            .Should().Be(new AliasToken(start, query));

    [Theory]
    [InlineData("echo ${env", 10)]
    [InlineData("git show HEAD@{1", 16)]
    [InlineData("configura lock", 14)]
    public void TheShellAndGitBraces_DoNotOpen(string text, int caret) =>
        AliasCompletion.FindToken(text, caret, PlaceholderCompletionViewModel.IsNameChar, '{').Should().BeNull();

    [Fact]
    public void Accepting_SwallowsTheClosingBraceAlreadyThere()
    {
        var edit = AliasCompletion.Accept("echo {wor} fim", new AliasToken(5, "wor"), 9, "{worktree}", PlaceholderCompletionViewModel.IsNameChar);

        edit.Text.Should().Be("echo {worktree} fim");
        edit.CaretIndex.Should().Be(15);
    }

    // --- A fonte ------------------------------------------------------------------

    [Fact]
    public void TheList_ShowsTheAliasesFirst_AndFiltersByNameOrDescription()
    {
        var source = new PlaceholderCompletionViewModel
        {
            Items =
            [
                new CommandPlaceholder("lock_eco_core_1010", "Banco copiado deste apelido", IsDatabaseAlias: true),
                .. CommandPlaceholders.Variables(),
            ],
        };

        source.UpdateCompletion("configura {", 11);

        source.IsCompletionOpen.Should().BeTrue();
        source.Suggestions[0].Text.Should().Be("{lock_eco_core_1010}");
        source.Suggestions.Select(item => item.Text).Should().Contain("{database}").And.Contain("{worktree}");

        source.UpdateCompletion("configura {banco", 16);

        source.Suggestions.Select(item => item.Text).Should().StartWith("{lock_eco_core_1010}").And.Contain("{database}");
        ((IAliasCompletionSource)source).ReplacementFor(source.Suggestions[0]).Should().Be("{lock_eco_core_1010}");

        source.UpdateCompletion("configura @x", 12);
        source.IsCompletionOpen.Should().BeFalse("o { é que abre esta lista");
    }

    [Fact]
    public void TheHint_DoesNotAskForAnAliasUsedInAGlobal()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetCatalog([new(Guid.CreateVersion7(), "@configura", "configura-ambiente {lock_eco_core_1010} {perfil}", null, At)]);
        commands.SetDatabaseAliases([Alias]);
        commands.SetEntries(["@configura perfil=dev"]);

        commands.Items[0].Hint.Should().Be("→ configura-ambiente {lock_eco_core_1010} dev");
        commands.Items[0].Placeholders.Items[0].Name.Should().Be("lock_eco_core_1010");
        commands.Items[0].Placeholders.Items[0].Description.Should().Contain("eco_core_1010");
    }

    // --- Na janela da tarefa -------------------------------------------------------

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    [AvaloniaFact]
    public async Task TypingBrace_InAPostWorktreeCommand_OffersTheAlias_AndAcceptingInsertsIt()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.ResultsByHandler[typeof(GetSavedDatabasesHandler)] = (IReadOnlyList<SavedDatabaseRow>)[Alias];
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List());
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList([GitBranch.Local("main")], GitBranch.Local("main"));

        var viewModel = new TaskNotesViewModel(
            runner, new FakeDirectoryProbe(), TestDevelopment.For(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskNotesViewModel>.Instance);
        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();
        viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
        viewModel.Developments.Selected!.Commands.Add();
        Settle(window);

        var input = Named<PostWorktreeCommandsView>(window, "SetupCommands")
            .GetVisualDescendants().OfType<CommandInputBox>().Single();
        var box = Named<TextBox>(input, "CommandBox");
        var item = viewModel.Developments.Selected!.Commands.Items[0];

        box.Focus();
        box.Text = "configura-ambiente {lock";
        box.CaretIndex = box.Text.Length;
        Settle(window);

        Named<Popup>(input, "PlaceholderPopup").IsOpen.Should().BeTrue();
        item.Completion.IsCompletionOpen.Should().BeFalse("o @ só abre no começo");
        item.Placeholders.SelectedSuggestion!.Text.Should().Be("{lock_eco_core_1010}");

        input.AcceptPlaceholder(item.Placeholders.SelectedSuggestion);
        Settle(window);

        item.Text.Should().Be("configura-ambiente {lock_eco_core_1010}");
        item.Placeholders.IsCompletionOpen.Should().BeFalse();
    }

    // --- Na janela de parâmetros ---------------------------------------------------

    [AvaloniaFact]
    public void TheParameterField_OffersTheCopiedDatabase_AndThePreviewShowsIt()
    {
        var task = TaskItem.Create("Feature", At);
        var development = task.BeginDevelopment(null, Repository, "origin/main", "feature/x", Repository + "-x", At);
        task.MarkDevelopmentReady(development.Id, At);
        task.SetDevelopmentDatabase(development.Id, TaskDevelopmentDatabase.Create(
            Guid.CreateVersion7(), "Local", "localhost", 5432, "dev", "lock_eco_core_1010_20261013_101500", null, At, "lock_eco_core_1010"));

        var entry = new QuickCommandEntry(
            Guid.CreateVersion7(), null, "Configurar", "@configura", "configura-ambiente {nomebanco}", CommandMode.Execute,
            null, true, false, [CommandParameterSpec.Plain("nomebanco")], null, null, false);
        var plan = new QuickCommandPlan(entry, Repository + "-x", CommandContext.For(task, development, null, aliases: ["lock_eco_core_1010"]));
        var viewModel = new QuickCommandPromptViewModel(plan);
        var window = new QuickCommandPromptWindow(viewModel);
        window.Show();
        Settle(window);

        var field = window.GetVisualDescendants().OfType<ParameterValueBox>().Single();
        var box = Named<TextBox>(field, "ValueBox");
        box.Focus();
        box.Text = "{";
        box.CaretIndex = 1;
        Settle(window);

        var placeholders = viewModel.Fields[0].Placeholders;
        Named<Popup>(field, "PlaceholderPopup").IsOpen.Should().BeTrue();
        placeholders.Suggestions[0].Text.Should().Be("{lock_eco_core_1010}");
        placeholders.Suggestions[0].Detail.Should().Be("= lock_eco_core_1010_20261013_101500");

        field.AcceptPlaceholder(placeholders.Suggestions[0]);
        Settle(window);

        viewModel.Fields[0].Value.Should().Be("{lock_eco_core_1010}");
        viewModel.PreviewText.Should().Be("configura-ambiente lock_eco_core_1010_20261013_101500");
        window.Close();
    }
}
