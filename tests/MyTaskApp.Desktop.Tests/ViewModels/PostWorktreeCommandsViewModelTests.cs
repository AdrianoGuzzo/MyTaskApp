using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A lista de comandos pós-Worktree na aba Desenvolvimento (ADR-028): editar,
/// reordenar, o autocomplete de <c>@</c>, e quando a tela pede para rodar.
/// </summary>
public class PostWorktreeCommandsViewModelTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly DateTimeOffset At = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly GitInstallation Installed = new(true, "2.51.0", "git");

    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("develop", "refs/remotes/origin/develop", isHead: true),
        GitBranch.RemoteTracking("origin", "develop"),
    ];

    private static readonly IReadOnlyList<DevelopmentCommandRow> Globals =
    [
        new(Guid.CreateVersion7(), "@build", "dotnet build", null, At),
        new(Guid.CreateVersion7(), "@npm-install", "npm install", "Dependências NPM", At),
        new(Guid.CreateVersion7(), "@restore", "dotnet restore", null, At),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private static TaskDevelopmentView View(TaskDevelopmentStatus status, params string[] commands) =>
        new(Repository, "origin/develop", "feature/x", Worktree, status, At, null, commands);

    private static CommandExecutionResult Exit(int code) => new(code, "", "", At, At.AddSeconds(3));

    private async Task<TaskDevelopmentViewModel> ActivatedAsync(TaskDevelopmentView? development = null)
    {
        _runner.Enqueue<GetTaskDevelopmentHandler>(development);
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = Globals;
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = Installed;
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);

        var viewModel = TestDevelopment.For(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Feature X", isReadOnly: false);
        await viewModel.ActivateAsync(Ct);

        viewModel.DirectoryText = Repository;
        await viewModel.InspectDirectoryAsync(Ct);

        return viewModel;
    }

    private static void Type(PostWorktreeCommandsViewModel commands, params string[] entries)
    {
        foreach (var entry in entries)
        {
            commands.Add();
            commands.Items[^1].Text = entry;
        }
    }

    private void CreatesTheWorktree()
    {
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] = new DevelopmentPlan(
            TaskId, Repository, Branches[0], "feature/x", Worktree, [], null);
        _runner.ResultsByHandler[typeof(StartDevelopmentHandler)] = View(TaskDevelopmentStatus.Ready, "@restore", "@build");
        _runner.ResultsByHandler[typeof(ValidateCommandEntriesHandler)] = (IReadOnlyList<ResolvedCommand>)[];
    }

    // --- Lista ---------------------------------------------------------------

    [Fact]
    public void Adding_Numbering_AndBlankLinesAreNotEntries()
    {
        var commands = new PostWorktreeCommandsViewModel();

        Type(commands, "@restore", "   ", "dotnet ef database update");

        commands.Items.Select(item => item.Number).Should().Equal(1, 2, 3);
        commands.Entries.Should().Equal("@restore", "dotnet ef database update");
        commands.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void MovingAndRemoving_ChangeTheOrderOfExecution()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetEntries(["@restore", "@npm-install", "@build"]);

        commands.Items[2].MoveUp();
        commands.Items[0].MoveDown();
        commands.Items.Single(item => item.Text == "@npm-install").Remove();

        commands.Entries.Should().Equal("@build", "@restore");
        commands.Items[0].CanMoveUp.Should().BeFalse();
        commands.Items[^1].CanMoveDown.Should().BeFalse();
        commands.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void AnAlias_ShowsWhatItRuns_OrWarnsWhenItDoesNotExist()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetCatalog(Globals);
        commands.SetEntries(["@build -c Release", "@sumiu", "git status"]);

        commands.Items[0].Hint.Should().Be("→ dotnet build -c Release");
        commands.Items[1].IsUnknownAlias.Should().BeTrue();
        commands.Items[1].Hint.Should().Contain("@sumiu não existe");
        commands.Items[2].HasHint.Should().BeFalse();
    }

    // --- Autocomplete ----------------------------------------------------------

    [Fact]
    public void TypingAt_ListsEveryGlobalCommand()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetCatalog(Globals);
        commands.Add();
        var completion = commands.Items[0].Completion;

        completion.UpdateCompletion("@", 1);

        completion.IsCompletionOpen.Should().BeTrue();
        completion.Suggestions.Select(item => item.Alias).Should().Equal("@build", "@npm-install", "@restore");
        completion.Suggestions[0].Command.Should().Be("dotnet build");
    }

    [Fact]
    public void TheQuery_FiltersByAliasOrCommand()
    {
        var completion = new CommandCompletionViewModel(new DevelopmentCommandCatalog { Rows = Globals });

        completion.UpdateCompletion("@npm", 4);
        completion.Suggestions.Select(item => item.Alias).Should().Equal("@npm-install");

        completion.UpdateCompletion("@rest", 5);
        completion.Suggestions.Select(item => item.Alias).Should().Equal("@restore");
    }

    [Fact]
    public void AnAtInTheMiddleOfTheLine_IsNotAnAlias()
    {
        var completion = new CommandCompletionViewModel(new DevelopmentCommandCatalog { Rows = Globals });

        completion.UpdateCompletion("npm run @b", 10);

        completion.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void AcceptingASuggestion_InsertsTheAlias_NotTheCommand()
    {
        var completion = new CommandCompletionViewModel(new DevelopmentCommandCatalog { Rows = Globals });
        completion.UpdateCompletion("@re", 3);
        MyTaskApp.Desktop.Notes.IAliasCompletionSource source = completion;

        source.ReplacementFor(completion.SelectedSuggestion).Should().Be("@restore");
        source.ReplacementFor(new CommandSuggestionViewModel(Globals[0])).Should().BeNull("não é desta lista");
    }

    // --- Execução, vista pela lista ---------------------------------------------

    [Fact]
    public void Progress_FollowsTheRunningStep_AndKeepsEachOutput()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetEntries(["@restore", "", "@build"]);

        commands.BeginRun();
        commands.Apply(new CommandStepProgress(0, CommandStepState.Running, "dotnet restore"));
        commands.Apply(new CommandStepProgress(0, CommandStepState.Running, Line: new("Restored", false)));
        commands.Apply(new CommandStepProgress(0, CommandStepState.Succeeded, "dotnet restore", Result: Exit(0)));
        commands.Apply(new CommandStepProgress(1, CommandStepState.Running, "dotnet build"));
        commands.Apply(new CommandStepProgress(1, CommandStepState.Running, Line: new("error CS0246", true)));

        commands.IsRunning.Should().BeTrue();
        commands.Items[0].IsDone.Should().BeTrue();
        commands.Items[0].Output.Lines.Should().ContainSingle().Which.Text.Should().Be("Restored");
        commands.Items[1].HasRunState.Should().BeFalse("a linha em branco não roda");
        commands.Items[2].IsRunning.Should().BeTrue();
        commands.Items[2].Output.Lines.Single().IsError.Should().BeTrue();
        commands.SelectedItem.Should().BeSameAs(commands.Items[2]);
        commands.CanEdit.Should().BeFalse();
    }

    [Fact]
    public void AFailedRun_MarksTheFailure_AndTheRestAsNotRun()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetEntries(["@restore", "@build", "@docker-up"]);
        commands.BeginRun();

        commands.Complete(new CommandRunSummary(
        [
            new(0, "@restore", "dotnet restore", CommandStepState.Succeeded, Exit(0), null),
            new(1, "@build", "dotnet build", CommandStepState.Failed, Exit(1), null),
            new(2, "@docker-up", null, CommandStepState.NotRun, null, null),
        ]));

        commands.IsRunning.Should().BeFalse();
        commands.Items.Select(item => item.StatusText).Should().Equal("Sucesso", "Falhou", "Não executado");
        commands.Items[1].Output.FooterText.Should().Contain("Exit Code 1");
        commands.RunMessage.Should().Contain("comando 2 (@build) falhou");
        commands.SelectedItem.Should().BeSameAs(commands.Items[1]);
    }

    [Fact]
    public void Cancel_CancelsTheRunToken()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetEntries(["npm run watch"]);
        var token = commands.BeginRun();

        commands.CancelCommand.Execute(null);

        token.IsCancellationRequested.Should().BeTrue();
    }

    // --- Integração com a criação do worktree -------------------------------------

    [Fact]
    public async Task AfterTheWorktreeIsCreated_TheCommandsRun()
    {
        var viewModel = await ActivatedAsync();
        Type(viewModel.Commands, "@restore", "@build");
        CreatesTheWorktree();
        _runner.ResultsByHandler[typeof(RunDevelopmentCommandsHandler)] = new CommandRunSummary(
        [
            new(0, "@restore", "dotnet restore", CommandStepState.Succeeded, Exit(0), null),
            new(1, "@build", "dotnet build", CommandStepState.Succeeded, Exit(0), null),
        ]);

        await viewModel.StartAsync();

        _runner.Invoked.Should().ContainInOrder(
            typeof(ValidateCommandEntriesHandler),
            typeof(PrepareDevelopmentHandler),
            typeof(StartDevelopmentHandler),
            typeof(RunDevelopmentCommandsHandler));
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        viewModel.Commands.RunSucceeded.Should().BeTrue();
        viewModel.Commands.IsDirty.Should().BeFalse("a lista foi gravada junto com o ambiente");
        viewModel.Commands.Items.Should().OnlyContain(item => item.IsDone);
    }

    [Fact]
    public async Task WithoutCommands_TheFlowIsTheSameAsBefore()
    {
        var viewModel = await ActivatedAsync();
        CreatesTheWorktree();

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        _runner.Invoked.Should().NotContain(typeof(ValidateCommandEntriesHandler));
        _runner.Invoked.Should().NotContain(typeof(RunDevelopmentCommandsHandler));
        viewModel.Commands.ShowRun.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheWorktreeFails_NoCommandRuns()
    {
        var viewModel = await ActivatedAsync();
        Type(viewModel.Commands, "@restore");
        CreatesTheWorktree();
        _runner.FailuresByHandler[typeof(StartDevelopmentHandler)] =
            new DevelopmentStepException(DevelopmentStep.CreateWorktree, "O Git não conseguiu criar o worktree.");

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Failed);
        _runner.Invoked.Should().NotContain(typeof(RunDevelopmentCommandsHandler));
    }

    [Fact]
    public async Task AnUnknownAlias_StopsBeforeCreatingAnything()
    {
        var viewModel = await ActivatedAsync();
        Type(viewModel.Commands, "@sumiu");
        CreatesTheWorktree();
        _runner.FailuresByHandler[typeof(ValidateCommandEntriesHandler)] =
            new DomainException("O comando @sumiu não existe. Cadastre-o em Comandos globais ou corrija o apelido.");

        await viewModel.StartAsync();

        viewModel.Message.Should().Contain("@sumiu não existe");
        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        _runner.Invoked.Should().NotContain(typeof(PrepareDevelopmentHandler));
    }

    [Fact]
    public async Task AReadyDevelopment_ShowsItsSavedList()
    {
        var viewModel = await ActivatedAsync(View(TaskDevelopmentStatus.Ready, "@restore", "@build"));

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        viewModel.Commands.Entries.Should().Equal("@restore", "@build");
        viewModel.Commands.IsDirty.Should().BeFalse();
        viewModel.Commands.CanEdit.Should().BeTrue();
        viewModel.Commands.Catalog.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunningAgain_WithAnEditedList_SavesFirst()
    {
        var viewModel = await ActivatedAsync(View(TaskDevelopmentStatus.Ready, "@restore"));
        Type(viewModel.Commands, "@build");
        _runner.ResultsByHandler[typeof(SetDevelopmentCommandsHandler)] =
            View(TaskDevelopmentStatus.Ready, "@restore", "@build");
        _runner.ResultsByHandler[typeof(RunDevelopmentCommandsHandler)] = new CommandRunSummary(
        [
            new(0, "@restore", "dotnet restore", CommandStepState.Succeeded, Exit(0), null),
            new(1, "@build", "dotnet build", CommandStepState.Succeeded, Exit(0), null),
        ]);

        await viewModel.RunCommandsAsync();

        _runner.Invoked.Should().ContainInOrder(
            typeof(SetDevelopmentCommandsHandler),
            typeof(RunDevelopmentCommandsHandler));
        viewModel.Commands.IsDirty.Should().BeFalse();
        viewModel.Commands.RunMessage.Should().Contain("2 comandos foram concluídos");
    }

    [Fact]
    public async Task AWorktreeThatDisappeared_IsReported_AndNothingIsLeftRunning()
    {
        var viewModel = await ActivatedAsync(View(TaskDevelopmentStatus.Ready, "@restore"));
        _runner.FailuresByHandler[typeof(RunDevelopmentCommandsHandler)] =
            new DomainException("A pasta do worktree não existe mais. Nenhum comando foi executado.");

        await viewModel.RunCommandsAsync();

        viewModel.Commands.IsRunning.Should().BeFalse();
        viewModel.Commands.RunMessage.Should().Contain("não existe mais");
        viewModel.Commands.Items[0].StatusText.Should().Be("Não executado");
    }
}
