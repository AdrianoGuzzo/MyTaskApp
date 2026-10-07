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
        TaskDevelopmentView? development = null,
        IReadOnlyList<TaskDevelopmentView>? developments = null,
        IReadOnlyList<GitTag>? tags = null,
        FakeTimeProvider? time = null)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.Enqueue<GetTaskDevelopmentsHandler>(developments ?? TestDevelopment.List(development));
        runner.ResultsByHandler[typeof(DetectGitHandler)] = git ?? new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(
            [GitBranch.Local("main"), GitBranch.RemoteTracking("origin", "main")],
            GitBranch.Local("main"),
            tags);

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner, timeProvider: time ?? new FakeTimeProvider()),
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
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
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

        // A aba Tempo (ADR-052) só aparece com o ViewModel dela, que esta montagem não passa.
        var tabs = window.GetVisualDescendants().OfType<TabItem>().Where(tab => tab.IsVisible).ToList();

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
            .Should().Contain(viewModel.Developments.Selected!.Instructions.Commands[0].Command);
        Named<Button>(window, "RecheckGitButton").Command.Should().NotBeNull();

        var copy = window.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("copyCommand"));
        copy.Command.Should().NotBeNull("o botão dentro da lista chega ao comando do painel");
        copy.CommandParameter.Should().Be(viewModel.Developments.Selected!.Instructions.Commands[0].Command);
    }

    /// <summary>O primeiro repositório é só o formulário: sem abas até existir um ambiente.</summary>
    [AvaloniaFact]
    public async Task WithoutEnvironments_TheRepositoryTabsStayHidden()
    {
        var (window, viewModel, _) = await ShowAsync();

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<DockPanel>(window, "RepositoryStrip").IsEffectivelyVisible.Should().BeFalse();
        Named<Border>(window, "SetupCard").IsEffectivelyVisible.Should().BeTrue();
    }

    /// <summary>
    /// Um repositório por aba (ADR-031): a aba escolhida troca o painel inteiro,
    /// e o botão de mais um repositório fica ao lado.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoRepositories_AreTwoTabs_AndChoosingOneSwapsThePanel()
    {
        var taskId = TaskNotesAliasTests.Row().TaskId;
        var ready = TestDevelopment.View(taskId, Domain.Tasks.TaskDevelopmentStatus.Ready, Repository);
        var removed = TestDevelopment.View(taskId, Domain.Tasks.TaskDevelopmentStatus.Removed, @"C:\Projects\ecossistema-api");
        var (window, viewModel, _) = await ShowAsync(developments: TestDevelopment.List(ready, removed));

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<DockPanel>(window, "RepositoryStrip").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "AddRepositoryButton").IsEffectivelyVisible.Should().BeTrue();
        Texts(Named<ListBox>(window, "RepositoryList")).Should().Contain(["ecossistema-core", "ecossistema-api"]);
        Named<Border>(window, "ReadyCard").IsEffectivelyVisible.Should().BeTrue();

        viewModel.Developments.Selected = viewModel.Developments.Items[1];
        await Task.Yield();
        Settle(window);

        Named<Border>(window, "ReadyCard").IsEffectivelyVisible.Should().BeFalse();
        Named<Border>(window, "SetupCard").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "ForgetButton").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task WithGit_TheFormShowsTheBranchesAndTheWorktreePath()
    {
        var (window, viewModel, _) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);

        viewModel.Developments.Selected!.DirectoryText = Repository;
        await viewModel.Developments.Selected!.InspectDirectoryAsync(CancellationToken.None);
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
        viewModel.Developments.Selected!.DirectoryText.Should().Be(Repository);
        window.IsVisible.Should().BeTrue("o Enter foi da lista, e não da janela");
    }

    /// <summary>A branch existente com PR aberta: o link aparece embaixo do aviso (ADR-047).</summary>
    [AvaloniaFact]
    public async Task AnExistingBranchWithAnOpenPullRequest_ShowsTheLink()
    {
        var time = new FakeTimeProvider();
        var (window, viewModel, runner) = await ShowAsync(time: time);
        runner.ResultsByHandler[typeof(FindPullRequestHandler)] = new PullRequestLookup(
            PullRequestSupport.Ready,
            new PullRequestInfo(47, "Corrige os animais", new Uri("https://github.com/acme/eco-core/pull/47"), IsDraft: false));
        await OpenDevelopmentTabAsync(window, viewModel);

        var environment = viewModel.Developments.Selected!;
        environment.DirectoryText = Repository;
        await environment.InspectDirectoryAsync(CancellationToken.None);
        environment.NewBranchName = "main";
        time.Advance(TaskDevelopmentViewModel.InspectionDelay);
        await environment.PendingPullRequest;
        Settle(window);

        var link = Named<Button>(window, "PullRequestLink");
        link.IsEffectivelyVisible.Should().BeTrue();
        link.Content.Should().Be("PR #47 aberta ↗");
        link.Command.Should().NotBeNull();
        ToolTip.GetTip(link).Should().BeOfType<string>().Which.Should().Contain("/pull/47");
        Texts(Named<StackPanel>(window, "PullRequestRow")).Should().Contain("Corrige os animais");
        Named<StackPanel>(window, "GhGuideHint").IsEffectivelyVisible.Should().BeFalse();
    }

    /// <summary>A consulta da branch falhou: o aviso e o "Tentar de novo", no lugar do link.</summary>
    [AvaloniaFact]
    public async Task AFailedPullRequestLookup_ShowsTheNoticeAndTheRetry()
    {
        var time = new FakeTimeProvider();
        var (window, viewModel, runner) = await ShowAsync(time: time);
        runner.ResultsByHandler[typeof(FindPullRequestHandler)] = new PullRequestLookup(PullRequestSupport.Failed);
        await OpenDevelopmentTabAsync(window, viewModel);

        var environment = viewModel.Developments.Selected!;
        environment.DirectoryText = Repository;
        await environment.InspectDirectoryAsync(CancellationToken.None);
        environment.NewBranchName = "main";
        time.Advance(TaskDevelopmentViewModel.InspectionDelay);
        await environment.PendingPullRequest;
        Settle(window);

        var notice = Named<StackPanel>(window, "PullRequestUnavailable");
        notice.IsEffectivelyVisible.Should().BeTrue();
        Texts(notice).Should().Contain(text => text != null && text.Contains("Não foi possível consultar o GitHub"));
        Named<Button>(window, "RetryPullRequestButton").Command.Should().BeSameAs(environment.RecheckGhCommand);
        Named<StackPanel>(window, "PullRequestRow").IsEffectivelyVisible.Should().BeFalse();
    }

    /// <summary>Repositório do GitHub sem o gh: o aviso, e o tutorial só depois do clique.</summary>
    [AvaloniaFact]
    public async Task WithoutGh_TheTutorialOpensOnRequest()
    {
        var time = new FakeTimeProvider();
        var (window, viewModel, runner) = await ShowAsync(time: time);
        runner.ResultsByHandler[typeof(FindPullRequestHandler)] = new PullRequestLookup(PullRequestSupport.CliMissing);
        await OpenDevelopmentTabAsync(window, viewModel);

        var environment = viewModel.Developments.Selected!;
        environment.DirectoryText = Repository;
        await environment.InspectDirectoryAsync(CancellationToken.None);
        time.Advance(TaskDevelopmentViewModel.InspectionDelay);
        await environment.PendingPullRequest;
        Settle(window);

        Named<StackPanel>(window, "GhGuideHint").IsEffectivelyVisible.Should().BeTrue();
        Named<Border>(window, "GhGuidePanel").IsEffectivelyVisible.Should().BeFalse();

        environment.ToggleGhGuideCommand.Execute(null);
        Settle(window);

        Named<Border>(window, "GhGuidePanel").IsEffectivelyVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text)
            .Should().Contain([environment.GhInstructions.Commands[0].Command, "gh auth login", "gh auth status"]);
        Named<Button>(window, "RecheckGhButton").Command.Should().NotBeNull();
    }

    [AvaloniaFact]
    public async Task AFailure_ShowsTheStep_AndTheGitErrorInTheDetails()
    {
        var (window, viewModel, runner) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);
        viewModel.Developments.Selected!.DirectoryText = Repository;
        await viewModel.Developments.Selected!.InspectDirectoryAsync(CancellationToken.None);
        runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] = new DevelopmentStepException(
            DevelopmentStep.Fetch,
            "Não foi possível atualizar as referências remotas.",
            new GitCommandResult("git fetch --all --prune", 128, "", "fatal: Could not resolve host"));

        await viewModel.Developments.Selected!.StartAsync();
        Settle(window);

        Named<Border>(window, "FailureCard").IsEffectivelyVisible.Should().BeTrue();
        Named<TextBlock>(window, "FailureReason").Text.Should().Be("Não foi possível atualizar as referências remotas.");
        Named<StackPanel>(window, "FailureDetails").IsEffectivelyVisible.Should().BeFalse();

        Named<ToggleButton>(window, "DetailsToggle").IsChecked = true;
        Settle(window);

        Named<StackPanel>(window, "FailureDetails").IsEffectivelyVisible.Should().BeTrue();
        Named<SelectableTextBlock>(window, "StandardErrorText").Text.Should().Contain("Could not resolve host");
    }

    /// <summary>ADR-043: o campo de tag só aparece quando o repositório tem tags.</summary>
    [AvaloniaFact]
    public async Task TheTagField_ShowsTheVersions_StartingAtNone()
    {
        var (window, viewModel, _) = await ShowAsync(tags: [new GitTag("v2.0.0"), new GitTag("v1.4.2")]);
        await OpenDevelopmentTabAsync(window, viewModel);

        viewModel.Developments.Selected!.DirectoryText = Repository;
        await viewModel.Developments.Selected!.InspectDirectoryAsync(CancellationToken.None);
        Settle(window);

        var box = Named<ComboBox>(window, "SourceTagBox");
        box.IsEffectivelyVisible.Should().BeTrue();
        box.SelectedItem.Should().BeSameAs(GitTagOptionViewModel.None);
        Named<TextBlock>(window, "SourceTagHint").IsEffectivelyVisible.Should().BeFalse();

        box.SelectedIndex = 2;
        Settle(window);

        viewModel.Developments.Selected!.SelectedTagOption!.Tag.Should().Be(new GitTag("v1.4.2"));
        Named<TextBlock>(window, "SourceTagHint").IsEffectivelyVisible.Should().BeTrue();
        Named<TextBlock>(window, "SourceTagHint").Text.Should().Contain("v1.4.2");
    }

    [AvaloniaFact]
    public async Task WithoutTags_TheTagFieldStaysHidden()
    {
        var (window, viewModel, _) = await ShowAsync();
        await OpenDevelopmentTabAsync(window, viewModel);

        viewModel.Developments.Selected!.DirectoryText = Repository;
        await viewModel.Developments.Selected!.InspectDirectoryAsync(CancellationToken.None);
        Settle(window);

        Named<ComboBox>(window, "SourceTagBox").IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AReadyTaskFromATag_ShowsTheTag()
    {
        var (window, viewModel, _) = await ShowAsync(development: ReadyDevelopment() with { SourceTag = "v1.4.2" });

        await OpenDevelopmentTabAsync(window, viewModel);

        var tag = Named<SelectableTextBlock>(window, "ReadySourceTag");
        tag.IsEffectivelyVisible.Should().BeTrue();
        tag.Text.Should().Be("v1.4.2");
        Texts(Named<Border>(window, "ReadyCard")).Should().Contain("Tag de origem");
    }

    [AvaloniaFact]
    public async Task AReadyTaskFromTheBranchTip_HidesTheTag()
    {
        var (window, viewModel, _) = await ShowAsync(development: ReadyDevelopment());

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<SelectableTextBlock>(window, "ReadySourceTag").IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AReadyTask_ShowsTheEnvironmentAndItsActions()
    {
        var ready = new TaskDevelopmentView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Repository, "origin/main", "feature/x", Repository + "-feature-x",
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
            Guid.CreateVersion7(), Guid.CreateVersion7(), Repository, "origin/main", "feature/x", Repository + "-feature-x",
            Domain.Tasks.TaskDevelopmentStatus.Ready, DateTimeOffset.UnixEpoch, null);

    /// <summary>O card do agente: PID e o botão que leva ao terminal (ADR-030).</summary>
    [AvaloniaFact]
    public async Task AReadyTaskWithTheAgentRunning_ShowsThePid_AndTheTerminalButton()
    {
        var (window, viewModel, runner) = await ShowAsync(development: ReadyDevelopment());
        runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = new AgentSessionView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "claude-code", "Claude Code", @"C:\claude.exe",
            Repository + "-feature-x", 15432, DateTimeOffset.UnixEpoch, null,
            Domain.Agents.AgentSessionStatus.Running, null);

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        var card = Named<Border>(window, "AgentCard");
        card.IsEffectivelyVisible.Should().BeTrue();
        Texts(card).Should().Contain(["Claude Code", "● Em execução"]);
        Named<SelectableTextBlock>(window, "AgentProcess").Text.Should().Be("PID 15432");
        Named<Button>(window, "FocusAgentButton").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "StartAgentButton").IsEffectivelyVisible.Should().BeFalse();
        Named<TextBox>(window, "AgentArgumentsBox").IsEffectivelyVisible.Should().BeFalse();
    }

    /// <summary>Antes de iniciar, os parâmetros aparecem prontos para editar.</summary>
    [AvaloniaFact]
    public async Task AReadyTaskWithoutAgent_ShowsTheArguments_FilledWithTheDefault()
    {
        var (window, viewModel, runner) = await ShowAsync(development: ReadyDevelopment());
        runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = new AgentCliStatus(
            "claude-code", "Claude Code", "claude",
            new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.4" },
            null,
            "--dangerously-skip-permissions");

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        var box = Named<TextBox>(window, "AgentArgumentsBox");
        box.IsEffectivelyVisible.Should().BeTrue();
        box.Text.Should().Be("--dangerously-skip-permissions");
        Named<TextBlock>(window, "AgentCommandPreview").Text.Should().Be("Roda: claude --dangerously-skip-permissions");
        Named<Button>(window, "StartAgentButton").IsEffectivelyVisible.Should().BeTrue();
    }

    /// <summary>ADR-040: modelo e esforço em listas, com o salvo marcado e o comando atualizado.</summary>
    [AvaloniaFact]
    public async Task TheModelAndEffortLists_ShowTheSavedChoice_AndChangeTheCommand()
    {
        var (window, viewModel, runner) = await ShowAsync(development: ReadyDevelopment());
        runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = new AgentCliStatus(
            "claude-code", "Claude Code", "claude",
            new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.4" },
            null,
            "--dangerously-skip-permissions",
            Model: "opus")
        {
            Models = [new("opus", "Opus", ["--model", "opus"]), new("sonnet", "Sonnet", ["--model", "sonnet"])],
            Efforts = [new("low", "Baixo", ["--effort", "low"]), new("max", "Máximo", ["--effort", "max"])],
        };

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        Named<Grid>(window, "AgentChoicesPanel").IsEffectivelyVisible.Should().BeTrue();
        var model = Named<ComboBox>(window, "AgentModelBox");
        model.ItemCount.Should().Be(3);
        model.SelectedIndex.Should().Be(1);
        var effort = Named<ComboBox>(window, "AgentEffortBox");
        effort.SelectedIndex.Should().Be(0);

        effort.SelectedIndex = 2;
        Settle(window);

        Named<TextBlock>(window, "AgentCommandPreview").Text
            .Should().Be("Roda: claude --dangerously-skip-permissions --model opus --effort max");
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
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        Named<StackPanel>(window, "AgentInstallPanel").IsEffectivelyVisible.Should().BeTrue();
        Texts(Named<Border>(window, "AgentCard")).Should().Contain("Claude Code não encontrado.");
        window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text)
            .Should().Contain("irm https://claude.ai/install.ps1 | iex");
        Named<Button>(window, "StartAgentButton").IsEffectivelyVisible.Should().BeFalse();
    }

    /// <summary>O texto gravado volta no campo, e "Executar direto" vem desmarcado.</summary>
    [AvaloniaFact]
    public async Task AnIdleAgent_ShowsTheSavedText_AndRunDirectlyUnchecked()
    {
        var development = ReadyDevelopment() with { AgentPrompt = "Implemente a tarefa" };
        var (window, viewModel, runner) = await ShowAsync(development: development);
        runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = new AgentCliStatus(
            "claude-code", "Claude Code", "claude",
            new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.4" },
            null);

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        var prompt = Named<TextBox>(window, "AgentPromptBox");
        prompt.IsEffectivelyVisible.Should().BeTrue();
        prompt.Text.Should().Be("Implemente a tarefa");

        var runDirectly = Named<CheckBox>(window, "AgentRunDirectly");
        runDirectly.IsChecked.Should().BeFalse();
        runDirectly.IsEnabled.Should().BeTrue();

        runDirectly.IsChecked = true;
        viewModel.Developments.Selected!.Agent.RunDirectly.Should().BeTrue();
    }

    /// <summary>
    /// O <c>@</c> no texto do agente (ADR-039), com teclado de verdade: Tab
    /// entra no outro ambiente da tarefa, a busca acha o arquivo e o Enter
    /// insere o caminho — sem quebrar a linha.
    /// </summary>
    [AvaloniaFact]
    public async Task TheAtInTheAgentText_GoesIntoAnotherEnvironment_AndInsertsTheFilePath()
    {
        var app = ReadyDevelopment();
        var api = ReadyDevelopment() with
        {
            Id = Guid.CreateVersion7(),
            RepositoryPath = @"C:\Projects\eco-api",
            WorktreePath = @"C:\Projects\eco-api-feature-x",
        };

        var (window, viewModel, runner) = await ShowAsync(developments: TestDevelopment.List(app, api));
        runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = new AgentCliStatus(
            "claude-code", "Claude Code", "claude",
            new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.4" },
            null);
        runner.ResultsByHandler[typeof(ListEnvironmentFilesHandler)] =
            new EnvironmentFiles(["README.md", "src/Api/Program.cs"]);

        await OpenDevelopmentTabAsync(window, viewModel);
        await viewModel.Developments.Selected!.Agent.RefreshAsync(CancellationToken.None);
        Settle(window);

        var prompt = Named<TextBox>(window, "AgentPromptBox");
        var popup = Named<Popup>(window, "AgentPromptPopup");
        var references = viewModel.Developments.PromptReferences;
        prompt.Focus();

        Write(window, "Compare com @eco-a");

        popup.IsOpen.Should().BeTrue();
        references.SelectedSuggestion!.Title.Should().Be("@eco-api");
        Texts((Control)popup.Child!).Should().Contain("@ecossistema-core");

        Press(window, Key.Tab, PhysicalKey.Tab);

        prompt.Text.Should().Be("Compare com @eco-api/");
        popup.IsOpen.Should().BeTrue();
        references.Suggestions.Select(item => item.Title).Should().Equal("src/", "README.md");
        prompt.IsFocused.Should().BeTrue("o Tab não pode tirar o foco da caixa");

        Write(window, "prog");
        Press(window, Key.Enter, PhysicalKey.Enter);

        prompt.Text.Should().Be(@"Compare com C:\Projects\eco-api-feature-x\src\Api\Program.cs");
        popup.IsOpen.Should().BeFalse();
        viewModel.Developments.Selected!.Agent.Prompt.Should().Be(prompt.Text);
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Write(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ATaskNotReady_HidesTheAgentCard()
    {
        var (window, viewModel, _) = await ShowAsync();

        await OpenDevelopmentTabAsync(window, viewModel);

        Named<Border>(window, "AgentCard").IsEffectivelyVisible.Should().BeFalse();
    }
}
