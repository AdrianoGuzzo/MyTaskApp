using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Desenvolvimento na janela de verdade (ADR-027). Pega o que só quebra
/// na tela: amarração com caminho errado, a aba que não aparece, o rodapé de
/// salvar sobrando na aba errada, o autocomplete do campo Diretório.
/// </summary>
public class TaskNotesDevelopmentRenderingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel, FakeUseCaseRunner Runner)> ShowAsync(
        GitInstallation? git = null,
        TaskDevelopmentView? development = null)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.Enqueue<GetTaskDevelopmentHandler>(development);
        runner.ResultsByHandler[typeof(DetectGitHandler)] = git ?? new GitInstallation(true, "2.51.0", "git");
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
        await viewModel.LoadAliasesAsync(CancellationToken.None);
        Settle(window);

        return (window, viewModel, runner);
    }

    private static async Task OpenDevelopmentTabAsync(TaskNotesWindow window, TaskNotesViewModel viewModel)
    {
        viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
        await viewModel.Development.ActivateAsync(CancellationToken.None);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<string?> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text);

    [AvaloniaFact]
    public async Task TheWindowHasTwoTabs_AndOpensOnTheNotes()
    {
        var (window, viewModel, _) = await ShowAsync();

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();

        tabs.Select(tab => tab.Header).Should().Equal("Anotação", "Desenvolvimento");
        viewModel.IsNotesTab.Should().BeTrue();
        window.GetVisualDescendants().OfType<TextBox>().Should().Contain(box => box.Name == "Editor");
    }

    [AvaloniaFact]
    public async Task TheSaveFooter_IsOnlyOnTheNotesTab()
    {
        var (window, viewModel, _) = await ShowAsync();
        var save = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Salvar"));
        save.IsEffectivelyVisible.Should().BeTrue();

        await OpenDevelopmentTabAsync(window, viewModel);

        save.IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task UnsavedNotes_AreFlaggedOnTheTabHeader()
    {
        var (window, viewModel, _) = await ShowAsync();

        viewModel.Text = "algo novo";
        Settle(window);

        window.GetVisualDescendants().OfType<TabItem>().First().Header.Should().Be("Anotação •");
    }

    [AvaloniaFact]
    public async Task WithoutGit_TheTabShowsTheInstallCommand_AndCheckAgain()
    {
        var (window, viewModel, _) = await ShowAsync(git: GitInstallation.Missing);

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<StackPanel>(window, "GitMissingPanel").IsEffectivelyVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text)
            .Should().Contain(viewModel.Development.Instructions.Commands[0].Command);
        Named<Button>(window, "RecheckGitButton").Command.Should().NotBeNull();

        var copy = window.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("copyCommand"));
        copy.Command.Should().NotBeNull("o botão dentro da lista chega ao comando do painel");
        copy.CommandParameter.Should().Be(viewModel.Development.Instructions.Commands[0].Command);
    }

    [AvaloniaFact]
    public async Task WithGit_TheFormShowsTheBranchesAndTheWorktreePath()
    {
        var (window, viewModel, _) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);

        viewModel.Development.DirectoryText = Repository;
        await viewModel.Development.InspectDirectoryAsync(CancellationToken.None);
        Settle(window);

        Named<TextBlock>(window, "RepositoryStatus").Text.Should().Be("✓ Repositório Git válido");
        Named<ComboBox>(window, "SourceBranchBox").SelectedItem.Should().BeOfType<BranchOptionViewModel>()
            .Which.Label.Should().Be("main");
        Named<SelectableTextBlock>(window, "WorktreePreview").Text
            .Should().Be(@"C:\Projects\ecossistema-core-feature-corrigir-problema-no-processamento-dos-animais");
        Named<Button>(window, "StartButton").IsEnabled.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TypingAnAliasInTheDirectoryField_InsertsThePath()
    {
        var (window, viewModel, _) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);

        var box = Named<TextBox>(window, "DirectoryBox");
        box.Focus();
        window.KeyTextInput("@ecossistema-c");
        Settle(window);

        Named<Popup>(window, "DirectoryPopup").IsOpen.Should().BeTrue();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);

        box.Text.Should().Be(Repository);
        viewModel.Development.DirectoryText.Should().Be(Repository);
        window.IsVisible.Should().BeTrue("o Enter foi da lista, e não da janela");
    }

    [AvaloniaFact]
    public async Task AFailure_ShowsTheStep_AndTheGitErrorInTheDetails()
    {
        var (window, viewModel, runner) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);
        viewModel.Development.DirectoryText = Repository;
        await viewModel.Development.InspectDirectoryAsync(CancellationToken.None);
        runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] = new DevelopmentStepException(
            DevelopmentStep.Fetch,
            "Não foi possível atualizar as referências remotas.",
            new GitCommandResult("git fetch --all --prune", 128, "", "fatal: Could not resolve host"));

        await viewModel.Development.StartAsync();
        Settle(window);

        Named<Border>(window, "FailureCard").IsEffectivelyVisible.Should().BeTrue();
        Named<TextBlock>(window, "FailureReason").Text.Should().Be("Não foi possível atualizar as referências remotas.");
        Named<StackPanel>(window, "FailureDetails").IsEffectivelyVisible.Should().BeFalse();

        Named<ToggleButton>(window, "DetailsToggle").IsChecked = true;
        Settle(window);

        Named<StackPanel>(window, "FailureDetails").IsEffectivelyVisible.Should().BeTrue();
        Named<SelectableTextBlock>(window, "StandardErrorText").Text.Should().Contain("Could not resolve host");
    }

    [AvaloniaFact]
    public async Task AReadyTask_ShowsTheEnvironmentAndItsActions()
    {
        var ready = new TaskDevelopmentView(
            Repository, "origin/main", "feature/x", Repository + "-feature-x",
            Domain.Tasks.TaskDevelopmentStatus.Ready, DateTimeOffset.UnixEpoch, null);
        var (window, viewModel, _) = await ShowAsync(development: ready);

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<Border>(window, "ReadyCard").IsEffectivelyVisible.Should().BeTrue();
        Named<SelectableTextBlock>(window, "ReadyWorktree").Text.Should().Be(Repository + "-feature-x");
        Texts(Named<Border>(window, "ReadyCard")).Should().Contain("✓ Ambiente pronto");

        var buttons = Named<Border>(window, "ReadyCard").GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => button.Content as string)
            .ToList();

        buttons.Should().Contain(["Abrir diretório", "Abrir terminal", "Copiar caminho", "Remover Worktree"]);
        Named<Border>(window, "SetupCard").IsEffectivelyVisible.Should().BeFalse();
    }

    private static TaskDevelopmentView ReadyDevelopment() =>
        new(
            Repository, "origin/main", "feature/x", Repository + "-feature-x",
            Domain.Tasks.TaskDevelopmentStatus.Ready, DateTimeOffset.UnixEpoch, null);

    /// <summary>O card do agente: PID e o botão que leva ao terminal (ADR-029).</summary>
    [AvaloniaFact]
    public async Task AReadyTaskWithTheAgentRunning_ShowsThePid_AndTheTerminalButton()
    {
        var (window, viewModel, runner) = await ShowAsync(development: ReadyDevelopment());
        runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = new AgentSessionView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "claude-code", "Claude Code", @"C:\claude.exe",
            Repository + "-feature-x", 15432, DateTimeOffset.UnixEpoch, null,
            Domain.Agents.AgentSessionStatus.Running, null);

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Development.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        var card = Named<Border>(window, "AgentCard");
        card.IsEffectivelyVisible.Should().BeTrue();
        Texts(card).Should().Contain(["Claude Code", "● Em execução"]);
        Named<SelectableTextBlock>(window, "AgentProcess").Text.Should().Be("PID 15432");
        Named<Button>(window, "FocusAgentButton").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "StartAgentButton").IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithoutTheAgentInstalled_TheCardShowsTheInstallCommand()
    {
        var (window, viewModel, runner) = await ShowAsync(development: ReadyDevelopment());
        runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = new AgentCliStatus(
            "claude-code", "Claude Code", "claude",
            CliDetectionResult.NotInstalled("Claude Code não encontrado."),
            new AgentCliInstallGuide(
                "Instalar o Claude Code no Windows",
                [new AgentCliInstallStep("No PowerShell:", "irm https://claude.ai/install.ps1 | iex")],
                new Uri("https://docs.claude.com/en/docs/claude-code/setup")));

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Development.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        Named<StackPanel>(window, "AgentInstallPanel").IsEffectivelyVisible.Should().BeTrue();
        Texts(Named<Border>(window, "AgentCard")).Should().Contain("Claude Code não encontrado.");
        window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text)
            .Should().Contain("irm https://claude.ai/install.ps1 | iex");
        Named<Button>(window, "StartAgentButton").IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ATaskNotReady_HidesTheAgentCard()
    {
        var (window, viewModel, _) = await ShowAsync();

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<Border>(window, "AgentCard").IsEffectivelyVisible.Should().BeFalse();
    }
}
