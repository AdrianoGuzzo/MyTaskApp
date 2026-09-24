using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// Os comandos pós-Worktree: gravar a lista, conferir os apelidos e rodar em
/// ordem dentro do worktree (ADR-028). Nenhum processo abre — o executor é falso.
/// </summary>
public class DevelopmentCommandsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDevelopmentCommandRepository _globals = new();
    private readonly FakeCommandExecutor _executor = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly List<CommandStepProgress> _progress = [];
    private readonly TaskItem _task;

    public DevelopmentCommandsHandlerTests()
    {
        _task = TaskItem.Create("Implementar feature X", Now);
        _tasks.Seed(_task);
        _disk.Existing.Add(Worktree);
        _globals.Seed("@restore", "dotnet restore");
        _globals.Seed("@build", "dotnet build");
        _globals.Seed("@npm-install", "npm install");
    }

    private void Ready(params string[] commands)
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/x", Worktree, Now);
        _task.SetDevelopmentCommands(_task.Developments[0].Id, commands, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
    }

    private RunDevelopmentCommandsHandler Runner() =>
        new(_tasks, _globals, _executor, _disk, NullLogger<RunDevelopmentCommandsHandler>.Instance);

    private Task<CommandRunSummary> RunAsync(CancellationToken? cancellationToken = null) =>
        Runner().HandleAsync(
            new RunDevelopmentCommands(_task.Id, _task.Developments[0].Id),
            new SynchronousProgress<CommandStepProgress>(_progress.Add),
            cancellationToken ?? Ct);

    [Fact]
    public async Task RunsEveryCommand_InOrder_InsideTheWorktree_ResolvingAliases()
    {
        Ready("@restore", "@npm-install", "dotnet ef database update", "@build");

        var summary = await RunAsync();

        _executor.Commands.Should().Equal(
            "dotnet restore", "npm install", "dotnet ef database update", "dotnet build");
        _executor.Requests.Should().OnlyContain(request => request.WorkingDirectory == Worktree);
        summary.Succeeded.Should().BeTrue();
        summary.Steps.Select(step => step.State).Should().AllBeEquivalentTo(CommandStepState.Succeeded);
    }

    [Fact]
    public async Task CapturesTheOutput_AndReportsEachLineWithItsStep()
    {
        Ready("@restore", "@build");
        _executor.Script("dotnet build", 0, ["Building..."], ["warning CS0168"]);

        var summary = await RunAsync();

        summary.Steps[1].Result!.StandardOutput.Should().Contain("Building...");
        summary.Steps[1].Result!.StandardError.Should().Contain("warning CS0168");
        summary.Steps[1].Result!.ExitCode.Should().Be(0);

        _progress.Where(report => report.Line is not null && report.Index == 1)
            .Select(report => (report.Line!.Text, report.Line.IsError))
            .Should().Equal(("Building...", false), ("warning CS0168", true));
    }

    [Fact]
    public async Task AFailure_StopsTheSequence_AndTheRestIsNotRun()
    {
        Ready("@restore", "@npm-install", "@build", "docker compose up -d");
        _executor.Script("dotnet build", 1, ["Building..."], ["error CS0246: ..."]);

        var summary = await RunAsync();

        _executor.Commands.Should().Equal("dotnet restore", "npm install", "dotnet build");
        summary.Steps.Select(step => step.State).Should().Equal(
            CommandStepState.Succeeded,
            CommandStepState.Succeeded,
            CommandStepState.Failed,
            CommandStepState.NotRun);
        summary.Steps[2].Result!.ExitCode.Should().Be(1);
        summary.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownAlias_FailsAtItsStep_WithoutReachingTheShell()
    {
        Ready("@restore", "@sumiu", "@build");

        var summary = await RunAsync();

        _executor.Commands.Should().Equal("dotnet restore");
        summary.Steps[1].State.Should().Be(CommandStepState.Failed);
        summary.Steps[1].Error.Should().Contain("@sumiu não existe");
        summary.Steps[2].State.Should().Be(CommandStepState.NotRun);
    }

    [Fact]
    public async Task AShellThatDoesNotStart_IsAFailure_NotAnException()
    {
        Ready("@restore", "@build");
        _executor.Unstartable.Add("dotnet restore");

        var summary = await RunAsync();

        summary.Steps[0].State.Should().Be(CommandStepState.Failed);
        summary.Steps[0].Error.Should().Contain("shell");
        summary.Steps[1].State.Should().Be(CommandStepState.NotRun);
    }

    [Fact]
    public async Task Cancelling_StopsTheCurrentCommand_KeepsItsOutput_AndSkipsTheRest()
    {
        Ready("@restore", "npm run watch", "@build");
        _executor.Script("npm run watch", 0, ["watching..."]);
        _executor.Hanging.Add("npm run watch");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = RunAsync(cancel.Token);

        await _executor.HangStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();

        var summary = await running;

        summary.WasCanceled.Should().BeTrue();
        summary.Steps.Select(step => step.State).Should().Equal(
            CommandStepState.Succeeded,
            CommandStepState.Canceled,
            CommandStepState.NotRun);
        summary.Steps[1].Result!.StandardOutput.Should().Contain("watching...");
        _executor.Commands.Should().NotContain("dotnet build");
    }

    [Fact]
    public async Task AnEmptyList_RunsNothing()
    {
        Ready();

        var summary = await RunAsync();

        summary.Steps.Should().BeEmpty();
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AWorktreeThatWasNotCreated_RunsNothing()
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/x", Worktree, Now);
        _task.SetDevelopmentCommands(_task.Developments[0].Id, ["@restore"], Now);
        _task.MarkDevelopmentFailed(_task.Developments[0].Id, "O Git não conseguiu criar o worktree.", Now);

        var run = () => RunAsync();

        await run.Should().ThrowAsync<DomainException>().WithMessage("*worktree pronto*");
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AWorktreeFolderThatDisappeared_RunsNothing()
    {
        Ready("@restore");
        _disk.Existing.Remove(Worktree);

        var run = () => RunAsync();

        await run.Should().ThrowAsync<DomainException>().WithMessage("*não existe mais*");
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AGlobalEditedAfterTheListWasSaved_RunsTheNewText()
    {
        Ready("@restore");
        var restore = _globals.Commands.Single(command => command.Alias == "@restore");
        restore.Update("@restore", "dotnet restore --locked-mode", null, Now);

        await RunAsync();

        _executor.Commands.Should().Equal("dotnet restore --locked-mode");
    }

    // --- Validar, gravar e começar --------------------------------------------

    [Fact]
    public async Task Validate_AcceptsAliasesThatExist_AndLiterals()
    {
        var resolved = await new ValidateCommandEntriesHandler(_globals).HandleAsync(
            new ValidateCommandEntries(["@restore", "git status"]), Ct);

        resolved.Select(step => step.Command).Should().Equal("dotnet restore", "git status");
    }

    [Fact]
    public async Task Validate_NamesEveryMissingAlias()
    {
        var validate = () => new ValidateCommandEntriesHandler(_globals).HandleAsync(
            new ValidateCommandEntries(["@restore", "@sumiu", "@outro"]), Ct);

        await validate.Should().ThrowAsync<DomainException>().WithMessage("*@sumiu, @outro não existem*");
    }

    [Fact]
    public async Task Set_ReplacesTheList_KeepingTheOrder()
    {
        Ready("@restore", "@build");

        var view = await new SetDevelopmentCommandsHandler(
                _tasks, _tasks, new FakeTimeProvider(Now), NullLogger<SetDevelopmentCommandsHandler>.Instance)
            .HandleAsync(new SetDevelopmentCommands(_task.Id, _task.Developments[0].Id, ["@build", " ", "@restore", "npm test"]), Ct);

        view.Commands.Should().Equal("@build", "@restore", "npm test");
        _task.Developments[0].Commands.Select(command => command.Order).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Start_SavesTheCommandsWithTheDevelopment()
    {
        var git = new FakeGitClient();
        var disk = new FakeDirectoryProbe();
        var plan = new DevelopmentPlan(
            _task.Id,
            FakeGitClient.Repository,
            GitBranch.RemoteTracking("origin", "develop"),
            "feature/x",
            Worktree,
            [],
            null);

        var view = await new StartDevelopmentHandler(
                _tasks, _tasks, git, disk, new FakeTimeProvider(Now), NullLogger<StartDevelopmentHandler>.Instance)
            .HandleAsync(new StartDevelopment(plan, Worktree, Commands: ["@restore", "@build"]), null, Ct);

        view.Commands.Should().Equal("@restore", "@build");
        _executor.Requests.Should().BeEmpty("gravar a lista não roda nada");
    }

    [Fact]
    public async Task Start_WhenTheWorktreeFails_LeavesNothingToRun()
    {
        var git = new FakeGitClient
        {
            AddWorktreeFailure = FakeGitClient.Failed("git worktree add", "fatal: invalid reference: develop"),
        };
        var plan = new DevelopmentPlan(
            _task.Id,
            FakeGitClient.Repository,
            GitBranch.RemoteTracking("origin", "develop"),
            "feature/x",
            Worktree,
            [],
            null);

        var start = () => new StartDevelopmentHandler(
                _tasks, _tasks, git, new FakeDirectoryProbe(), new FakeTimeProvider(Now),
                NullLogger<StartDevelopmentHandler>.Instance)
            .HandleAsync(new StartDevelopment(plan, Worktree, Commands: ["@restore"]), null, Ct);

        await start.Should().ThrowAsync<DevelopmentStepException>();

        var run = () => RunAsync();
        await run.Should().ThrowAsync<DomainException>();
        _executor.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCommand_TestsAGlobalByItsAlias_InTheChosenFolder()
    {
        var summary = await new RunCommandHandler(
                _globals, _executor, _disk, NullLogger<RunCommandHandler>.Instance)
            .HandleAsync(new RunCommand("@build", Worktree), null, Ct);

        summary.Succeeded.Should().BeTrue();
        _executor.Requests.Should().ContainSingle()
            .Which.Should().Be(new CommandExecutionRequest("dotnet build", Worktree));
    }

    [Fact]
    public async Task RunCommand_InAFolderThatDoesNotExist_RunsNothing()
    {
        var run = () => new RunCommandHandler(
                _globals, _executor, _disk, NullLogger<RunCommandHandler>.Instance)
            .HandleAsync(new RunCommand("@build", @"C:\nao\existe"), null, Ct);

        await run.Should().ThrowAsync<DomainException>().WithMessage("*não existe*");
        _executor.Requests.Should().BeEmpty();
    }

    /// <summary>Entrega na hora, na thread de quem reporta.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
