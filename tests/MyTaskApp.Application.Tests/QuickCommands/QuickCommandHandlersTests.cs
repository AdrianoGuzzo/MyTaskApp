using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.QuickCommands;

/// <summary>
/// Preparar e executar um comando rápido no worktree da tarefa (ADR-051).
/// Nenhum processo abre: o executor, o terminal e o rastreador são falsos.
/// </summary>
public class QuickCommandHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-validacao";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDevelopmentCommandRepository _globals = new();
    private readonly FakeTagQuery _tagQuery = new();
    private readonly FakeCommandExecutionRepository _executions = new();
    private readonly FakeCommandExecutor _executor = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeTerminalCommandLauncher _terminals;
    private readonly FakeCommandExecutionWatcher _watcher = new();
    private readonly FakeTerminalWindowManager _windows = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly TaskItem _task = TaskItem.Create("Implementar validação de antimicrobianos", Now);
    private readonly Guid _tagId = Guid.CreateVersion7();
    private readonly Guid _directoryId = Guid.CreateVersion7();
    private readonly List<CommandOutputLine> _lines = [];

    private readonly DevelopmentCommand _run;
    private readonly DevelopmentCommand _test;

    public QuickCommandHandlersTests()
    {
        _terminals = new FakeTerminalCommandLauncher(_processes, Now);
        _tasks.Seed(_task);
        _task.SetTags([_tagId]);
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/validacao", Worktree, Now);
        _task.MarkDevelopmentReady(Development.Id, Now);
        _disk.Existing.Add(Worktree);

        _run = _globals.Seed(
            "@run",
            "dotnet run",
            settings: new DevelopmentCommandSettings("Executar aplicação", CommandMode.Terminal));
        _test = _globals.Seed("@test", "dotnet test", settings: new DevelopmentCommandSettings("Testes"));
    }

    private TaskDevelopment Development => _task.Developments[0];

    private Guid Bind(DevelopmentCommand command, int order = 0, string? commandOverride = null, string? folder = null)
    {
        var binding = new TagDirectoryCommandRow(
            Guid.CreateVersion7(), _directoryId, command.Id, command.Alias, command.Name, command.Command,
            order, true, commandOverride, folder);

        var existing = _tagQuery.CommandDirectories.FirstOrDefault(directory => directory.Id == _directoryId);

        _tagQuery.CommandDirectories.Clear();
        _tagQuery.CommandDirectories.Add(new CommandDirectoryRow(
            _directoryId, _tagId, "ECO CORE", "@ecossistema-core", FakeGitClient.Repository,
            [.. existing?.Commands ?? [], binding]));

        return binding.Id;
    }

    private RunQuickCommandHandler Runner() =>
        new(_tasks, _tagQuery, _globals, _executions, _executions, _executor, _terminals, _processes, _watcher, _disk,
            _time, NullLogger<RunQuickCommandHandler>.Instance);

    private Task<CommandExecutionView> RunAsync(
        DevelopmentCommand command,
        Guid? bindingId = null,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken? cancellationToken = null) =>
        Runner().HandleAsync(
            new RunQuickCommand(_task.Id, Development.Id, command.Id, bindingId, values),
            new SynchronousProgress<CommandOutputLine>(_lines.Add),
            cancellationToken ?? Ct);

    private PrepareQuickCommandHandler Preparer() => new(_tasks, _tagQuery, _globals, _disk);

    // ---- Execução escondida ------------------------------------------------

    [Fact]
    public async Task Execute_RunsInTheWorktree_AndRecordsRunningThenCompleted()
    {
        var binding = Bind(_test);
        _executor.Script("dotnet test", 0, ["Aprovado!"]);

        var view = await RunAsync(_test, binding);

        _executor.Requests.Should().ContainSingle().Which.Should().Be(new CommandExecutionRequest("dotnet test", Worktree));
        view.Status.Should().Be(CommandExecutionStatus.Completed);
        view.ExitCode.Should().Be(0);
        view.Output.Should().Contain("Aprovado!");
        view.BindingId.Should().Be(binding);
        view.CommandName.Should().Be("Testes");
        _lines.Should().ContainSingle().Which.Text.Should().Be("Aprovado!");
        _executions.Saved.Select(statuses => statuses.Single()).Should().Equal(
            CommandExecutionStatus.Running,
            CommandExecutionStatus.Completed);
        _watcher.SeenInProcess.Should().BeEmpty();
        _watcher.Notified.Should().AllBeEquivalentTo(_task.Id);
    }

    [Fact]
    public async Task Execute_ANonZeroExit_IsFailed_WithTheExitCode()
    {
        _executor.Script("dotnet test", 1, errors: ["1 teste falhou"]);

        var view = await RunAsync(_test);

        view.Status.Should().Be(CommandExecutionStatus.Failed);
        view.ExitCode.Should().Be(1);
        view.ErrorOutput.Should().Contain("1 teste falhou");
    }

    [Fact]
    public async Task Execute_Cancelled_IsStopped_KeepingTheOutput()
    {
        _executor.Hanging.Add("dotnet test");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var running = RunAsync(_test, cancellationToken: cancellation.Token);
        await _executor.HangStarted.Task;
        await cancellation.CancelAsync();

        var view = await running;

        view.Status.Should().Be(CommandExecutionStatus.Stopped);
        _executions.Executions.Single().Status.Should().Be(CommandExecutionStatus.Stopped);
    }

    [Fact]
    public async Task Execute_IsReachableByCancel_WhileItRuns()
    {
        _executor.Hanging.Add("dotnet test");

        var running = RunAsync(_test);
        await _executor.HangStarted.Task;

        var cancelled = await new CancelQuickCommandHandler(_watcher)
            .HandleAsync(new CancelQuickCommand(_executions.Executions.Single().Id), Ct);

        cancelled.Should().BeTrue();
        (await running).Status.Should().Be(CommandExecutionStatus.Stopped);
    }

    [Fact]
    public async Task Execute_AShellThatDoesNotStart_IsFailed_NotAnException()
    {
        _executor.Unstartable.Add("dotnet test");

        var view = await RunAsync(_test);

        view.Status.Should().Be(CommandExecutionStatus.Failed);
        view.FailureReason.Should().Be("Não foi possível iniciar o shell.");
    }

    // ---- Terminal -----------------------------------------------------------

    [Fact]
    public async Task Terminal_OpensTheLineInTheWorktree_AndWatchesTheProcess()
    {
        var binding = Bind(_run);

        var view = await RunAsync(_run, binding);

        _terminals.Launched.Should().ContainSingle()
            .Which.Should().Be(new TerminalCommandRequest("dotnet run", Worktree, KeepOpen: true));
        view.Status.Should().Be(CommandExecutionStatus.Running);
        view.ProcessId.Should().Be(_terminals.LastProcessId);
        view.HasTerminal.Should().BeTrue();
        _watcher.Watched.Should().ContainSingle().Which.ProcessId.Should().Be(_terminals.LastProcessId);
        _executions.Saved[0].Single().Should().Be(CommandExecutionStatus.Queued, "grava antes de abrir");
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_LaunchFailure_IsAFailedExecution()
    {
        _terminals.FailWith = "Abrir um terminal pelo app ainda não é suportado neste sistema.";

        var view = await RunAsync(_run);

        view.Status.Should().Be(CommandExecutionStatus.Failed);
        view.FailureReason.Should().Contain("ainda não é suportado");
        _watcher.Watched.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_ExitedBeforeTheCheck_IsCompleted_WithoutExitCode()
    {
        _terminals.ExitsImmediately = true;

        var view = await RunAsync(_run);

        view.Status.Should().Be(CommandExecutionStatus.Completed);
        view.ExitCode.Should().BeNull();
        _watcher.Watched.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_AlreadyOpen_IsRefused_PointingToShowTerminal()
    {
        await RunAsync(_run);

        var again = () => RunAsync(_run);

        (await again.Should().ThrowAsync<DomainException>()).WithMessage("*Mostrar terminal*");
        _terminals.Launched.Should().HaveCount(1);
    }

    [Fact]
    public async Task Terminal_ClosedOutside_CanRunAgain()
    {
        var first = await RunAsync(_run);
        _processes.Exit(first.ProcessId!.Value);

        var second = await RunAsync(_run);

        second.Status.Should().Be(CommandExecutionStatus.Running);
        _executions.Executions.First(execution => execution.Id == first.Id).Status
            .Should().Be(CommandExecutionStatus.Completed);
    }

    // ---- Resolução ----------------------------------------------------------

    [Fact]
    public async Task TheOverride_AndItsFolder_AreWhatRuns()
    {
        var folder = Path.Combine(Worktree, "src", "Eco.Web");
        _disk.Existing.Add(folder);
        var binding = Bind(_test, commandOverride: "dotnet test --no-build \"{worktree}\"", folder: "src/Eco.Web");

        await RunAsync(_test, binding);

        _executor.Requests.Single().Should().Be(new CommandExecutionRequest($"dotnet test --no-build \"{Worktree}\"", folder));
        _test.Command.Should().Be("dotnet test");
    }

    [Fact]
    public async Task Parameters_AreFilled_WithDefaultsForBlanks()
    {
        var command = _globals.Seed(
            "@profile",
            "dotnet run --project {project} --launch-profile {profile}",
            settings: new DevelopmentCommandSettings(
                "Perfil",
                Parameters:
                [
                    new("project", "Projeto"),
                    new("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"]),
                ]));

        await RunAsync(command, values: new Dictionary<string, string> { ["project"] = "Eco.Web", ["profile"] = "" });

        _executor.Commands.Should().Equal("dotnet run --project Eco.Web --launch-profile Development");
    }

    [Fact]
    public async Task AMissingRequiredParameter_IsRefused_BeforeAnythingIsSaved()
    {
        var command = _globals.Seed("@sync", "eco-sync {banco}");

        var run = () => RunAsync(command);

        (await run.Should().ThrowAsync<DomainException>()).WithMessage("Informe banco.");
        _executions.Executions.Should().BeEmpty();
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AChoiceOutsideTheOptions_IsRefused()
    {
        var command = _globals.Seed(
            "@env",
            "deploy {ambiente}",
            settings: new DevelopmentCommandSettings(Parameters: [new("ambiente", Type: CommandParameterType.Choice, Options: ["dev"])]));

        var run = () => RunAsync(command, values: new Dictionary<string, string> { ["ambiente"] = "prod" });

        await run.Should().ThrowAsync<DomainException>();
        _executions.Executions.Should().BeEmpty();
    }

    [Fact]
    public async Task ATitleWithAnAmpersand_IsRefused_WhenTheCommandUsesIt()
    {
        _task.Update("Validar & publicar", null, _task.Priority);
        var command = _globals.Seed("@note", "echo {task.title}");

        var run = () => RunAsync(command);

        (await run.Should().ThrowAsync<DomainException>()).WithMessage("*{task.title}*\"&\"*");
        (await RunAsync(_test)).Status.Should().Be(CommandExecutionStatus.Completed, "quem não usa o título roda");
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsRefused()
    {
        var binding = Bind(_test, folder: "src/naoexiste");

        var run = () => RunAsync(_test, binding);

        (await run.Should().ThrowAsync<DomainException>()).WithMessage("*src/naoexiste*");
    }

    [Fact]
    public async Task AWorktreeNotReady_RunsNothing()
    {
        _task.MarkDevelopmentRemoved(Development.Id, Now);

        var run = () => RunAsync(_test);

        await run.Should().ThrowAsync<DomainException>();
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ADeletedGlobal_IsReported()
    {
        _globals.Remove(_test);

        var run = () => RunAsync(_test);

        (await run.Should().ThrowAsync<DomainException>()).WithMessage("*não está mais disponível*excluído*");
    }

    [Fact]
    public async Task History_KeepsTheLastTwenty()
    {
        for (var index = 0; index < RunQuickCommandHandler.HistoryLimit + 5; index++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await RunAsync(_test);
        }

        _executions.Executions.Should().HaveCount(RunQuickCommandHandler.HistoryLimit);
        _executions.Executions.Max(execution => execution.StartedAt).Should().Be(_time.GetUtcNow());
    }

    // ---- Preparar e ler -----------------------------------------------------

    [Fact]
    public async Task Prepare_ReturnsTheLine_TheFolder_AndWhatToAsk()
    {
        var command = _globals.Seed(
            "@deploy",
            "deploy {ambiente} --branch {branch}",
            settings: new DevelopmentCommandSettings("Deploy", RequiresConfirmation: true));

        var plan = await Preparer().HandleAsync(new PrepareQuickCommand(_task.Id, Development.Id, command.Id, null), Ct);

        plan.WorkingDirectory.Should().Be(Worktree);
        plan.NeedsPrompt.Should().BeTrue();
        plan.Entry.Parameters.Select(parameter => parameter.Name).Should().Equal("ambiente");
        plan.Build(new Dictionary<string, string> { ["ambiente"] = "dev" }).Line
            .Should().Be("deploy dev --branch feature/validacao");
        plan.Build(null).Line.Should().BeNull();
        _executions.Executions.Should().BeEmpty();
    }

    [Fact]
    public async Task Prepare_WithNothingToAsk_DoesNotNeedThePrompt()
    {
        var plan = await Preparer().HandleAsync(new PrepareQuickCommand(_task.Id, Development.Id, _test.Id, null), Ct);

        plan.NeedsPrompt.Should().BeFalse();
        plan.Build(null).Line.Should().Be("dotnet test");
    }

    [Fact]
    public async Task Get_ListsTheButtons_TheGlobals_AndTheRecentRuns()
    {
        Bind(_run, 0);
        Bind(_test, 1);
        await RunAsync(_test);

        var view = await Getter().HandleAsync(new GetQuickCommands(_task.Id, Development.Id), Ct);

        view.Commands.Select(entry => entry.Name).Should().Equal("Executar aplicação", "Testes");
        view.Globals.Should().HaveCount(2);
        view.Recent.Should().ContainSingle().Which.CommandId.Should().Be(_test.Id);
    }

    [Fact]
    public async Task ADirectoryOnlyCommand_IsAButtonHere_ButNotAGlobal_AndRuns()
    {
        var front = _globals.SeedForDirectory(
            _directoryId,
            "Front-end",
            "npm run dev",
            new DevelopmentCommandSettings(WorkingDirectory: "web"));
        _disk.Existing.Add(Path.Combine(Worktree, "web"));
        Bind(_run, 0);
        var binding = Bind(front, 1);

        var view = await Getter().HandleAsync(new GetQuickCommands(_task.Id, Development.Id), Ct);
        await RunAsync(front, binding);

        view.Commands.Select(entry => entry.Name).Should().Equal("Executar aplicação", "Front-end");
        view.Commands[1].Alias.Should().BeNull();
        view.Globals.Select(global => global.Id).Should().NotContain(front.Id, "\"+ Executar comando…\" só oferece globais");
        _executor.Requests.Should().ContainSingle()
            .Which.Should().Be(new CommandExecutionRequest("npm run dev", Path.Combine(Worktree, "web")));
    }

    [Fact]
    public async Task ADirectoryOnlyCommand_IsOnlyLoadedForItsOwnDirectory()
    {
        var elsewhere = _globals.SeedForDirectory(Guid.CreateVersion7(), "Outro", "npm start");
        Bind(_run, 0);
        Bind(elsewhere, 1);

        var view = await Getter().HandleAsync(new GetQuickCommands(_task.Id, Development.Id), Ct);
        var run = () => RunAsync(elsewhere, Guid.CreateVersion7());

        view.Commands.Select(entry => entry.Name).Should().Equal("Executar aplicação");
        (await run.Should().ThrowAsync<DomainException>()).WithMessage("*não está mais disponível*");
    }

    [Fact]
    public async Task Get_EndsATerminalClosedWhileNobodyWatched()
    {
        var opened = await RunAsync(_run);
        _processes.Exit(opened.ProcessId!.Value);

        var view = await Getter().HandleAsync(new GetQuickCommands(_task.Id, Development.Id), Ct);

        view.Recent.Single().Status.Should().Be(CommandExecutionStatus.Completed);
    }

    [Fact]
    public async Task Get_ForAnEnvironmentNotReady_IsEmpty()
    {
        _task.MarkDevelopmentRemoved(Development.Id, Now);

        var view = await Getter().HandleAsync(new GetQuickCommands(_task.Id, Development.Id), Ct);

        view.Should().BeSameAs(QuickCommandsView.Empty);
    }

    [Fact]
    public async Task Focus_BringsTheTerminalForward()
    {
        var opened = await RunAsync(_run);

        var result = await new FocusCommandExecutionHandler(
                _executions, _executions, _processes, _windows, _watcher, _time,
                NullLogger<FocusCommandExecutionHandler>.Instance)
            .HandleAsync(new FocusCommandExecution(opened.Id), Ct);

        result.Focused.Should().BeTrue();
        _windows.Focused.Should().Equal(opened.ProcessId!.Value);
    }

    [Fact]
    public async Task Focus_AClosedTerminal_EndsIt_AndFocusesNothing()
    {
        var opened = await RunAsync(_run);
        _processes.Exit(opened.ProcessId!.Value);

        var result = await new FocusCommandExecutionHandler(
                _executions, _executions, _processes, _windows, _watcher, _time,
                NullLogger<FocusCommandExecutionHandler>.Instance)
            .HandleAsync(new FocusCommandExecution(opened.Id), Ct);

        result.Focused.Should().BeFalse();
        result.Execution.Status.Should().Be(CommandExecutionStatus.Completed);
        _windows.Focused.Should().BeEmpty();
    }

    [Fact]
    public async Task End_KeepsTheExitCode_OnlyWhenTheTerminalClosesWithTheCommand()
    {
        var keepOpen = await RunAsync(_run);
        var closes = _globals.Seed(
            "@once",
            "dotnet build",
            settings: new DevelopmentCommandSettings("Uma vez", CommandMode.Terminal, KeepTerminalOpen: false));
        var once = await RunAsync(closes);

        var end = new EndCommandExecutionHandler(
            _executions, _executions, _watcher, _time, NullLogger<EndCommandExecutionHandler>.Instance);
        await end.HandleAsync(new EndCommandExecution(keepOpen.Id, -1073741510), Ct);
        await end.HandleAsync(new EndCommandExecution(once.Id, 2), Ct);

        var executions = _executions.Executions.ToDictionary(execution => execution.Id);
        executions[keepOpen.Id].ExitCode.Should().BeNull("com /k, o exit code é de quem fechou a janela");
        executions[keepOpen.Id].Status.Should().Be(CommandExecutionStatus.Completed);
        executions[once.Id].ExitCode.Should().Be(2);
        executions[once.Id].Status.Should().Be(CommandExecutionStatus.Failed);
    }

    private GetQuickCommandsHandler Getter() =>
        new(_tasks, _tagQuery, _globals, _executions, _executions, _processes, _watcher, _time);

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
