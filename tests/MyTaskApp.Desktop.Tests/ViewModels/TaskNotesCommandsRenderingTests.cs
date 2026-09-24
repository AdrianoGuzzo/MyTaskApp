using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Os comandos pós-Worktree na janela de verdade (ADR-028): a seção aparece,
/// cada comando é uma caixa, o <c>@</c> abre a lista e o terminal mostra o output.
/// </summary>
public class TaskNotesCommandsRenderingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static readonly DateTimeOffset At = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<DevelopmentCommandRow> Globals =
    [
        new(Guid.CreateVersion7(), "@build", "dotnet build", null, At),
        new(Guid.CreateVersion7(), "@restore", "dotnet restore", null, At),
    ];

    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel)> ShowAsync(
        TaskDevelopmentView? development = null)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = Globals;
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List(development));
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(
            [GitBranch.Local("main"), GitBranch.RemoteTracking("origin", "main")],
            GitBranch.Local("main"));

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskNotesViewModel>.Instance);

        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();

        viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
        Settle(window);

        return (window, viewModel);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<string?> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text);

    [AvaloniaFact]
    public async Task TheForm_HasTheCommandsSection_AndAddingCreatesAnInput()
    {
        var (window, viewModel) = await ShowAsync();
        var section = Named<PostWorktreeCommandsView>(window, "SetupCommands");

        Texts(section).Should().Contain("Comandos pós-Worktree");
        section.GetVisualDescendants().OfType<CommandInputBox>().Should().BeEmpty();

        viewModel.Developments.Selected!.Commands.Add();
        viewModel.Developments.Selected!.Commands.Add();
        Settle(window);

        section.GetVisualDescendants().OfType<CommandInputBox>().Should().HaveCount(2);
    }

    [AvaloniaFact]
    public async Task TypingAt_OpensTheGlobalCommands_AndAcceptingInsertsTheAlias()
    {
        var (window, viewModel) = await ShowAsync();
        viewModel.Developments.Selected!.Commands.Add();
        Settle(window);

        var input = Named<PostWorktreeCommandsView>(window, "SetupCommands")
            .GetVisualDescendants().OfType<CommandInputBox>().Single();
        var box = Named<TextBox>(input, "CommandBox");
        var item = viewModel.Developments.Selected!.Commands.Items[0];

        box.Focus();
        box.Text = "@re";
        box.CaretIndex = 3;
        Settle(window);

        item.Completion.IsCompletionOpen.Should().BeTrue();
        Named<Popup>(input, "CommandPopup").IsOpen.Should().BeTrue();
        item.Completion.Suggestions.Select(suggestion => suggestion.Alias).Should().Equal("@restore");

        input.AcceptSuggestion(item.Completion.SelectedSuggestion);
        Settle(window);

        item.Text.Should().Be("@restore");
        item.Hint.Should().Be("→ dotnet restore");
        item.Completion.IsCompletionOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AfterARun_TheTerminalShowsTheOutputAndTheExitCode()
    {
        var (window, viewModel) = await ShowAsync(new TaskDevelopmentView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Repository, "origin/main", "feature/x", Worktree, TaskDevelopmentStatus.Ready, At, null, ["@build"]));
        var commands = viewModel.Developments.Selected!.Commands;

        commands.BeginRun();
        commands.Apply(new CommandStepProgress(0, CommandStepState.Running, "dotnet build"));
        commands.Apply(new CommandStepProgress(0, CommandStepState.Running, Line: new("Building...", false)));
        commands.Apply(new CommandStepProgress(0, CommandStepState.Running, Line: new("error CS0246: tipo", true)));
        commands.Complete(new CommandRunSummary(
        [
            new(0, "@build", "dotnet build", CommandStepState.Failed,
                new CommandExecutionResult(1, "Building...", "error CS0246: tipo", At, At.AddSeconds(4)), null),
        ]));
        Settle(window);

        var section = Named<PostWorktreeCommandsView>(window, "ReadyCommands");
        var terminal = Named<CommandOutputView>(section, "Terminal");

        terminal.IsVisible.Should().BeTrue();
        var lines = terminal.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text).ToList();
        lines.Should().Contain("> dotnet build").And.Contain("Building...").And.Contain("error CS0246: tipo");
        Texts(terminal).Should().Contain(text => text != null && text.Contains("Exit Code 1"));
        Named<TextBlock>(section, "RunMessage").Text.Should().Contain("falhou");
    }
}
