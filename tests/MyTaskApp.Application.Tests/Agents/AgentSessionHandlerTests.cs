using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>
/// Abrir o agente no worktree, lembrar do processo e reencontrá-lo (ADR-030).
/// Nenhum terminal abre — launcher, processos e janelas são falsos.
/// </summary>
public class AgentSessionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeTerminalLauncher _launcher;
    private readonly FakeTerminalWindowManager _windows = new();
    private readonly FakeAgentCliProvider _claude = new();
    private readonly RecordingAgentSessionWatcher _watcher = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly AgentCliProviders _providers;
    private readonly TaskItem _task;

    public AgentSessionHandlerTests()
    {
        _launcher = new FakeTerminalLauncher(_processes, Now);
        _providers = new AgentCliProviders([_claude]);
        _task = TaskItem.Create("Implementar autenticação", Now);
        _tasks.Seed(_task);
        _disk.Existing.Add(Worktree);

        // O ambiente existe desde o começo, ainda sem worktree pronto: é nele que
        // as sessões moram (ADR-031).
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/123", Worktree, Now);
    }

    private void Ready()
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/123", Worktree, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
    }

    private StartAgentSessionHandler Start() =>
        new(
            _tasks,
            _sessions,
            _tasks,
            _providers,
            _launcher,
            _processes,
            _watcher,
            _disk,
            _time,
            NullLogger<StartAgentSessionHandler>.Instance);

    private GetTaskAgentSessionHandler Get() =>
        new(_sessions, _tasks, _providers, _processes, _watcher, _time);

    private FocusAgentSessionHandler Focus() =>
        new(_sessions, _tasks, _providers, _processes, _windows, _watcher, _time, NullLogger<FocusAgentSessionHandler>.Instance);

    private EndAgentSessionHandler End() =>
        new(_sessions, _tasks, _watcher, _time, NullLogger<EndAgentSessionHandler>.Instance);

    private ReconcileAgentSessionsHandler Reconcile() =>
        new(_sessions, _tasks, _processes, _watcher, _time, NullLogger<ReconcileAgentSessionsHandler>.Instance);

    private Task<AgentSessionView> StartAsync() => Start().HandleAsync(new StartAgentSession(_task.Id, _task.Developments[0].Id), Ct);

    private AgentSession Persisted(int processId, bool alive, Guid? taskId = null)
    {
        var session = AgentSession.Create(
            taskId ?? _task.Id,
            taskId is null ? _task.Developments[0].Id : Guid.CreateVersion7(),
            "claude-code", FakeAgentCliProvider.Executable, Worktree, Now);
        session.MarkRunning(processId, Now);
        _sessions.Seed(session);

        if (alive)
        {
            _processes.Run(processId, Now);
        }

        return session;
    }

    // --- Detecção ----------------------------------------------------------

    [Fact]
    public async Task Detect_Installed_BringsPathAndVersion_AndNoGuide()
    {
        var status = await new DetectAgentCliHandler(_providers).HandleAsync(new DetectAgentCli(), Ct);

        status.Name.Should().Be("Claude Code");
        status.Command.Should().Be("claude");
        status.Detection.IsInstalled.Should().BeTrue();
        status.Detection.ExecutablePath.Should().Be(FakeAgentCliProvider.Executable);
        status.Detection.Version.Should().Be("2.1.0");
        status.InstallGuide.Should().BeNull();
    }

    [Fact]
    public async Task Detect_NotInstalled_BringsTheInstallGuide()
    {
        _claude.Installed = false;

        var status = await new DetectAgentCliHandler(_providers).HandleAsync(new DetectAgentCli(), Ct);

        status.Detection.IsInstalled.Should().BeFalse();
        status.InstallGuide.Should().Be(FakeAgentCliProvider.WindowsGuide);
    }

    [Fact]
    public async Task AnUnknownAgent_IsRefused()
    {
        await FluentActions.Awaiting(() => new DetectAgentCliHandler(_providers).HandleAsync(new DetectAgentCli("gemini"), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    // --- Iniciar -----------------------------------------------------------

    [Fact]
    public async Task Start_OpensTheAgentInsideTheWorktree_AndRecordsThePid()
    {
        Ready();

        var view = await StartAsync();

        _launcher.Launched.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Executable = FakeAgentCliProvider.Executable, WorkingDirectory = Worktree });

        view.Status.Should().Be(AgentSessionStatus.Running);
        view.ProcessId.Should().Be(15432);
        view.TaskId.Should().Be(_task.Id);
        view.ProviderName.Should().Be("Claude Code");
        view.WorkingDirectory.Should().Be(Worktree);
        view.StartedAt.Should().Be(Now);

        var session = _sessions.Sessions.Should().ContainSingle().Subject;
        session.TaskItemId.Should().Be(_task.Id);
        session.ProcessStartedAt.Should().Be(Now);

        _watcher.Watches.Should().ContainSingle().Which.ProcessId.Should().Be(15432);
        _watcher.Changes.Should().Contain(_task.Id);
    }

    [Fact]
    public async Task Start_WithoutText_OpensTheAgentEmpty()
    {
        Ready();

        await StartAsync();

        _claude.LastContext.Should().BeEquivalentTo(new { Prompt = (string?)null, RunDirectly = false });
        _task.Developments[0].AgentPrompt.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WithText_HandsTextAndMode_ToTheAgent_AndKeepsTheText(bool runDirectly)
    {
        Ready();
        var developmentId = _task.Developments[0].Id;

        await Start().HandleAsync(
            new StartAgentSession(_task.Id, developmentId, Prompt: "  Implemente a tarefa  ", RunDirectly: runDirectly),
            Ct);

        _claude.LastContext.Should().BeEquivalentTo(new { Prompt = "Implemente a tarefa", RunDirectly = runDirectly });
        _task.GetDevelopment(developmentId).AgentPrompt.Should().Be("Implemente a tarefa");
    }

    [Fact]
    public async Task Start_WithoutTheAgentInstalled_CreatesNoSession_AndOpensNothing()
    {
        Ready();
        _claude.Installed = false;

        await FluentActions.Awaiting(StartAsync)
            .Should().ThrowAsync<DomainException>()
            .WithMessage("Claude Code não encontrado.");

        _sessions.Sessions.Should().BeEmpty();
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithoutAWorktree_IsRefused()
    {
        await FluentActions.Awaiting(StartAsync).Should().ThrowAsync<DomainException>();

        _launcher.Launched.Should().BeEmpty();
        _claude.Detections.Should().Be(0);
    }

    [Fact]
    public async Task Start_WithAWorktreeStillBeingCreated_IsRefused()
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/123", Worktree, Now);

        await FluentActions.Awaiting(StartAsync).Should().ThrowAsync<DomainException>();

        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithTheWorktreeFolderGone_IsRefused()
    {
        Ready();
        _disk.Existing.Clear();

        await FluentActions.Awaiting(StartAsync)
            .Should().ThrowAsync<DomainException>()
            .WithMessage("*não existe mais*");

        _sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenTheTerminalFailsToOpen_RecordsAFailedSession_NotARunningOne()
    {
        Ready();
        _launcher.FailWith = "O executável não foi encontrado.";

        var view = await StartAsync();

        view.Status.Should().Be(AgentSessionStatus.Failed);
        view.FailureReason.Should().Be("O executável não foi encontrado.");
        view.ProcessId.Should().BeNull();
        _watcher.Watches.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenTheAgentExitsRightAway_RecordsItAsExited()
    {
        Ready();
        _launcher.ExitsImmediately = true;

        var view = await StartAsync();

        view.Status.Should().Be(AgentSessionStatus.Exited);
        view.ProcessId.Should().Be(15432);
        view.EndedAt.Should().Be(Now);
        _watcher.Watches.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithAnAgentAlreadyRunning_IsRefused_AndDoesNotOpenAnother()
    {
        Ready();
        await StartAsync();

        await FluentActions.Awaiting(StartAsync)
            .Should().ThrowAsync<DomainException>()
            .WithMessage("*já tem um Claude Code aberto*");

        _launcher.Launched.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_AfterThePreviousAgentExited_OpensANewSession()
    {
        Ready();
        var first = Persisted(1000, alive: false);

        var view = await StartAsync();

        first.Status.Should().Be(AgentSessionStatus.Exited);
        view.Status.Should().Be(AgentSessionStatus.Running);
        view.SessionId.Should().NotBe(first.Id);
    }

    /// <summary>Cada tarefa fica com o seu processo.</summary>
    [Fact]
    public async Task SeveralTasks_EachKeepTheirOwnProcess()
    {
        Ready();
        var other = TaskItem.Create("Corrigir consulta SQL", Now);
        other.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/121", Worktree + "-121", Now);
        other.MarkDevelopmentReady(other.Developments[0].Id, Now);
        _tasks.Seed(other);
        _disk.Existing.Add(Worktree + "-121");

        var first = await StartAsync();
        var second = await Start().HandleAsync(new StartAgentSession(other.Id, other.Developments[0].Id), Ct);

        first.ProcessId.Should().NotBe(second.ProcessId);
        (await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct))!.ProcessId.Should().Be(first.ProcessId);
        (await Get().HandleAsync(new GetTaskAgentSession(other.Id, other.Developments[0].Id), Ct))!.ProcessId.Should().Be(second.ProcessId);
        _launcher.Launched.Select(launch => launch.WorkingDirectory).Should().Equal(Worktree, Worktree + "-121");
    }

    /// <summary>Um agente por ambiente (ADR-031): cada repositório da tarefa tem o seu.</summary>
    [Fact]
    public async Task EachRepositoryOfTheTask_HasItsOwnAgent()
    {
        Ready();
        var api = AnotherReadyRepository();

        var first = await StartAsync();
        var second = await Start().HandleAsync(new StartAgentSession(_task.Id, api.Id), Ct);

        second.Status.Should().Be(AgentSessionStatus.Running);
        first.ProcessId.Should().NotBe(second.ProcessId);
        _launcher.Launched.Select(launch => launch.WorkingDirectory).Should().Equal(Worktree, api.WorktreePath);
        (await Get().HandleAsync(new GetTaskAgentSession(_task.Id, api.Id), Ct))!.ProcessId.Should().Be(second.ProcessId);

        await FluentActions.Awaiting(() => Start().HandleAsync(new StartAgentSession(_task.Id, api.Id), Ct))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("Este ambiente já tem um Claude Code aberto*");
    }

    [Fact]
    public async Task Focus_ReachesTheAgentOfTheChosenRepository()
    {
        Ready();
        var api = AnotherReadyRepository();
        await StartAsync();
        var second = await Start().HandleAsync(new StartAgentSession(_task.Id, api.Id), Ct);

        await Focus().HandleAsync(new FocusAgentSession(_task.Id, api.Id), Ct);

        _windows.Focused.Should().Equal(second.ProcessId!.Value);
    }

    private TaskDevelopment AnotherReadyRepository()
    {
        const string ApiWorktree = @"C:\Projects\ecossistema-api-feature-123";

        var api = _task.BeginDevelopment(null, @"C:\Projects\ecossistema-api", "origin/develop", "feature/123", ApiWorktree, Now);
        _task.MarkDevelopmentReady(api.Id, Now);
        _disk.Existing.Add(ApiWorktree);
        return api;
    }

    // --- Consultar ---------------------------------------------------------

    [Fact]
    public async Task Get_ATaskWithoutSession_ReturnsNothing()
    {
        (await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Get_ARunningSessionWithItsProcessAlive_StaysRunning()
    {
        Persisted(15432, alive: true);

        var view = await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct);

        view!.Status.Should().Be(AgentSessionStatus.Running);
        view.IsActive.Should().BeTrue();
        _tasks.SaveCount.Should().Be(0);
    }

    /// <summary>Não confiar só no banco: o processo sumiu, a sessão acabou.</summary>
    [Fact]
    public async Task Get_ARunningSessionWhoseProcessIsGone_IsEndedAndSaved()
    {
        Persisted(15432, alive: false);

        var view = await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct);

        view!.Status.Should().Be(AgentSessionStatus.Exited);
        view.EndedAt.Should().Be(Now);
        _tasks.SaveCount.Should().Be(1);
        _watcher.Changes.Should().Contain(_task.Id);
    }

    /// <summary>Mesmo PID, outro processo: o Windows reaproveitou o número.</summary>
    [Fact]
    public async Task Get_APidReusedByAnotherProcess_IsNotTheSession()
    {
        Persisted(15432, alive: false);
        _processes.Run(15432, Now.AddHours(3));

        var view = await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct);

        view!.Status.Should().Be(AgentSessionStatus.Exited);
    }

    [Fact]
    public async Task Get_AFinishedSession_IsShownAsFinished()
    {
        var session = Persisted(15432, alive: false);
        session.MarkExited(Now.AddMinutes(35));

        var view = await Get().HandleAsync(new GetTaskAgentSession(_task.Id, _task.Developments[0].Id), Ct);

        view!.Status.Should().Be(AgentSessionStatus.Exited);
        view.EndedAt.Should().Be(Now.AddMinutes(35));
        _tasks.SaveCount.Should().Be(0);
    }

    // --- Abrir terminal ----------------------------------------------------

    [Fact]
    public async Task Focus_BringsTheTerminalOfThatProcess()
    {
        Persisted(15432, alive: true);

        var result = await Focus().HandleAsync(new FocusAgentSession(_task.Id), Ct);

        result.Focused.Should().BeTrue();
        _windows.Focused.Should().Equal(15432);
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task Focus_WhenTheProcessIsGone_EndsTheSession_AndOpensNothing()
    {
        Persisted(15432, alive: false);

        var result = await Focus().HandleAsync(new FocusAgentSession(_task.Id), Ct);

        result.Focused.Should().BeFalse();
        result.Session.Status.Should().Be(AgentSessionStatus.Exited);
        _windows.Focused.Should().BeEmpty();
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task Focus_WhenTheWindowIsNotFound_SaysSo()
    {
        Persisted(15432, alive: true);
        _windows.Finds = false;

        var result = await Focus().HandleAsync(new FocusAgentSession(_task.Id), Ct);

        result.Focused.Should().BeFalse();
        result.Session.Status.Should().Be(AgentSessionStatus.Running);
    }

    // --- Encerrar e reconciliar -------------------------------------------

    [Fact]
    public async Task End_MarksTheSessionExited_AndTellsTheScreens()
    {
        var session = Persisted(15432, alive: true);

        await End().HandleAsync(new EndAgentSession(session.Id), Ct);

        session.Status.Should().Be(AgentSessionStatus.Exited);
        _watcher.Changes.Should().Equal(_task.Id);
    }

    [Fact]
    public async Task End_AnAlreadyEndedSession_ChangesNothing()
    {
        var session = Persisted(15432, alive: false);
        session.MarkExited(Now.AddMinutes(5));

        await End().HandleAsync(new EndAgentSession(session.Id), Ct);

        session.EndedAt.Should().Be(Now.AddMinutes(5));
        _watcher.Changes.Should().BeEmpty();
    }

    /// <summary>O app foi reaberto com o Claude ainda aberto.</summary>
    [Fact]
    public async Task Reconcile_APersistedSessionWithItsProcessAlive_IsWatchedAgain()
    {
        var session = Persisted(15432, alive: true);

        var result = await Reconcile().HandleAsync(new ReconcileAgentSessions(), Ct);

        result.Should().Be(new AgentReconciliation(1, 0));
        session.Status.Should().Be(AgentSessionStatus.Running);
        _watcher.Watches.Should().ContainSingle().Which.SessionId.Should().Be(session.Id);
    }

    /// <summary>O Claude foi fechado com o app fechado.</summary>
    [Fact]
    public async Task Reconcile_APersistedSessionWithoutProcess_IsEnded_AndNoNewSessionIsCreated()
    {
        var session = Persisted(15432, alive: false);

        var result = await Reconcile().HandleAsync(new ReconcileAgentSessions(), Ct);

        result.Should().Be(new AgentReconciliation(0, 1));
        session.Status.Should().Be(AgentSessionStatus.Exited);
        _sessions.Sessions.Should().ContainSingle();
        _launcher.Launched.Should().BeEmpty();
        _watcher.Changes.Should().Equal(_task.Id);
    }

    /// <summary>
    /// Sem PID há pouco tempo é uma sessão que outra operação está abrindo
    /// agora; sem PID há muito tempo é o app que caiu no meio.
    /// </summary>
    [Fact]
    public async Task Reconcile_ASessionStuckStarting_IsEndedOnlyAfterTheGrace()
    {
        var session = AgentSession.Create(_task.Id, _task.Developments[0].Id, "claude-code", FakeAgentCliProvider.Executable, Worktree, Now);
        _sessions.Seed(session);

        await Reconcile().HandleAsync(new ReconcileAgentSessions(), Ct);
        session.Status.Should().Be(AgentSessionStatus.Starting);

        _time.Advance(AgentSessionReconciler.StartingGrace + TimeSpan.FromSeconds(1));
        await Reconcile().HandleAsync(new ReconcileAgentSessions(), Ct);

        session.Status.Should().Be(AgentSessionStatus.Exited);
    }
}
