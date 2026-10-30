using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
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
    private readonly FakeAgentSettingsStore _settings = new();
    private readonly FakeAgentEventEndpoint _events = new();
    private readonly RecordingAgentAttentionPresenter _presenter = new();
    private readonly AgentCliProviders _providers;
    private readonly TaskItem _task;
    private ITerminalSessions _terminals = NoTerminalSessions.Instance;
    private readonly FakeAgentTerminalClient _agentTerminal = new();
    private readonly AgentTerminalWindows _terminalWindows;

    public AgentSessionHandlerTests()
    {
        _launcher = new FakeTerminalLauncher(_processes, Now);
        _terminalWindows = new AgentTerminalWindows(_agentTerminal, _launcher, _windows, _processes, NullLogger<AgentTerminalWindows>.Instance);
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
            _settings,
            _events,
            _terminals,
            _time,
            NullLogger<StartAgentSessionHandler>.Instance);

    private GetTaskAgentSessionHandler Get() =>
        new(_sessions, _tasks, _providers, _processes, _watcher, _time);

    private FocusAgentSessionHandler Focus() =>
        new(_sessions, _tasks, _providers, _processes, _windows, _terminalWindows, _tasks, _watcher, _time, NullLogger<FocusAgentSessionHandler>.Instance);

    private OpenAgentSessionHandler Open() =>
        new(Start(), _agentTerminal, _terminalWindows, _sessions, _tasks, _providers, _settings, _tasks, NullLogger<OpenAgentSessionHandler>.Instance);

    private EndAgentSessionHandler End() =>
        new(_sessions, _tasks, _watcher, _presenter, _time, NullLogger<EndAgentSessionHandler>.Instance);

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
        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

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

        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Detection.IsInstalled.Should().BeFalse();
        status.InstallGuide.Should().Be(FakeAgentCliProvider.WindowsGuide);
    }

    [Fact]
    public async Task AnUnknownAgent_IsRefused()
    {
        await FluentActions.Awaiting(() => new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli("gemini"), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Detect_BringsTheDefaultArguments_WhileNothingWasSaved()
    {
        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Arguments.Should().Be("--dangerously-skip-permissions");
    }

    [Fact]
    public async Task Detect_BringsTheSavedArguments_EvenWhenEmpty()
    {
        _settings.Arguments["claude-code"] = "";

        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Arguments.Should().BeEmpty();
    }

    // --- Parâmetros --------------------------------------------------------

    private Task<AgentSessionView> StartWithAsync(string? arguments) =>
        Start().HandleAsync(new StartAgentSession(_task.Id, _task.Developments[0].Id, Arguments: arguments), Ct);

    [Fact]
    public async Task Start_WithoutArguments_UsesTheDefault_AndSavesNothing()
    {
        Ready();

        await StartAsync();

        _launcher.Launched.Should().ContainSingle()
            .Which.Arguments.Should().Equal("--dangerously-skip-permissions");
        _settings.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_WithoutArguments_UsesTheSavedOnes()
    {
        Ready();
        _settings.Arguments["claude-code"] = "--model opus";

        await StartAsync();

        _launcher.Launched.Should().ContainSingle().Which.Arguments.Should().Equal("--model", "opus");
    }

    [Fact]
    public async Task Start_WithArguments_LaunchesWithThem_AndMakesThemTheDefault()
    {
        Ready();

        await StartWithAsync("  --dangerously-skip-permissions --append-system-prompt \"seja breve\" ");

        _launcher.Launched.Should().ContainSingle()
            .Which.Arguments.Should().Equal("--dangerously-skip-permissions", "--append-system-prompt", "seja breve");
        _settings.Arguments["claude-code"].Should().Be("--dangerously-skip-permissions --append-system-prompt \"seja breve\"");
    }

    [Fact]
    public async Task Start_WithTheSameArguments_DoesNotSaveAgain()
    {
        Ready();

        await StartWithAsync("--dangerously-skip-permissions");

        _settings.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_WithEmptyArguments_OpensTheBareAgent_AndRemembersIt()
    {
        Ready();

        await StartWithAsync("");

        _launcher.Launched.Should().ContainSingle().Which.Arguments.Should().BeEmpty();
        _settings.Arguments["claude-code"].Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithInvalidArguments_IsRefused_BeforeAnythingIsRecorded()
    {
        Ready();

        await FluentActions.Awaiting(() => StartWithAsync("--append-system-prompt \"sem fim"))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("Aspas sem fechar nos parâmetros do agente.");

        _launcher.Launched.Should().BeEmpty();
        _sessions.Sessions.Should().BeEmpty();
        _settings.SaveCount.Should().Be(0);
    }

    // --- Modelo e esforço (ADR-040) ------------------------------------------

    private Task<AgentSessionView> StartWithAsync(string? model, string? effort, string? arguments = null) =>
        Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Arguments: arguments, Model: model, Effort: effort),
            Ct);

    [Fact]
    public async Task Detect_BringsTheOptions_AndTheAgentDefault_WhileNothingWasChosen()
    {
        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Models.Select(option => option.Value).Should().Equal("opus", "sonnet");
        status.Efforts.Select(option => option.Value).Should().Equal("low", "high");
        status.Model.Should().BeEmpty();
        status.Effort.Should().BeEmpty();
    }

    [Fact]
    public async Task Detect_BringsTheSavedChoice_OnlyWhileTheAgentStillOffersIt()
    {
        _settings.Models["claude-code"] = "opus";
        _settings.Efforts["claude-code"] = "ultra";

        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Model.Should().Be("opus");
        status.Effort.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithoutChoosing_AddsNothing()
    {
        Ready();

        await StartAsync();

        _launcher.Launched.Should().ContainSingle()
            .Which.Arguments.Should().Equal("--dangerously-skip-permissions");
        _settings.Models.Should().BeEmpty();
        _settings.Efforts.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithModelAndEffort_AddsThemAfterTheArguments_AndMakesThemTheDefault()
    {
        Ready();

        await StartWithAsync(" OPUS ", "high");

        _launcher.Launched.Should().ContainSingle().Which.Arguments.Should().Equal(
            "--dangerously-skip-permissions", "--model", "opus", "--effort", "high");
        _settings.Models["claude-code"].Should().Be("opus");
        _settings.Efforts["claude-code"].Should().Be("high");
    }

    [Fact]
    public async Task Start_WithoutChoosing_UsesTheSavedChoice()
    {
        Ready();
        _settings.Models["claude-code"] = "sonnet";
        _settings.Efforts["claude-code"] = "low";

        await StartAsync();

        _launcher.Launched.Should().ContainSingle().Which.Arguments.Should().Equal(
            "--dangerously-skip-permissions", "--model", "sonnet", "--effort", "low");
    }

    [Fact]
    public async Task Start_WithTheDefaultChosen_DropsTheSavedChoice()
    {
        Ready();
        _settings.Models["claude-code"] = "sonnet";
        _settings.Efforts["claude-code"] = "low";

        await StartWithAsync("", "");

        _launcher.Launched.Should().ContainSingle()
            .Which.Arguments.Should().Equal("--dangerously-skip-permissions");
        _settings.Models["claude-code"].Should().BeEmpty();
        _settings.Efforts["claude-code"].Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WithAnUnknownModel_IsRefused_BeforeAnythingIsRecorded()
    {
        Ready();

        await FluentActions.Awaiting(() => StartWithAsync("gpt", null, arguments: "--verbose"))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("Modelo desconhecido para o Claude Code: gpt.");

        _launcher.Launched.Should().BeEmpty();
        _sessions.Sessions.Should().BeEmpty();
        _settings.Models.Should().BeEmpty();
        _settings.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_WithInvalidArguments_DoesNotKeepTheNewChoice()
    {
        Ready();

        await FluentActions.Awaiting(() => StartWithAsync("opus", "high", arguments: "\"sem fim"))
            .Should().ThrowAsync<DomainException>();

        _settings.Models.Should().BeEmpty();
        _settings.Efforts.Should().BeEmpty();
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

    /// <summary>A conversa sem terminal não tem janela: a tela abre a conversa, e o handler não procura nada.</summary>
    [Fact]
    public async Task Focus_AConversationWithoutTerminal_ReturnsIt_WithoutLookingForAWindow()
    {
        var session = AgentSession.CreateHeadless(
            _task.Id, _task.Developments[0].Id, "claude-code", FakeAgentCliProvider.Executable, Worktree,
            Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null, null, Now, "Pixel 8");
        session.MarkRunning(15432, Now);
        _sessions.Seed(session);
        _processes.Run(15432, Now);

        var result = await Focus().HandleAsync(new FocusAgentSession(_task.Id), Ct);

        result.Focused.Should().BeFalse();
        result.Session.IsHeadless.Should().BeTrue();
        result.Session.Mode.Should().Be(AgentSessionMode.Headless);
        result.Session.RemoteOrigin.Should().Be("Pixel 8");
        _windows.Focused.Should().BeEmpty();
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

    // --- Acompanhamento pelos hooks (ADR-037) -------------------------------

    /// <summary>
    /// A associação é explícita: o processo nasce sabendo a tarefa, o ambiente,
    /// a sessão e o segredo — o banco guarda só o hash do segredo.
    /// </summary>
    [Fact]
    public async Task Start_WithMonitoring_GivesTheProcessItsIdentity_AndKeepsOnlyTheHash()
    {
        Ready();

        var view = await StartAsync();

        var session = _sessions.Sessions.Should().ContainSingle().Subject;
        var environment = _launcher.Launched.Should().ContainSingle().Subject.Environment!;

        environment[AgentMonitoringEnvironment.TaskId].Should().Be(_task.Id.ToString());
        environment[AgentMonitoringEnvironment.DevelopmentId].Should().Be(_task.Developments[0].Id.ToString());
        environment[AgentMonitoringEnvironment.AgentSessionId].Should().Be(session.Id.ToString());
        environment[AgentMonitoringEnvironment.WorktreePath].Should().Be(Worktree);
        environment[AgentMonitoringEnvironment.Branch].Should().Be("feature/123");
        environment[AgentMonitoringEnvironment.EventsUrl].Should().Be(FakeAgentEventEndpoint.Listening.ToString());

        var token = environment[AgentMonitoringEnvironment.HookToken];
        session.IsMonitored.Should().BeTrue();
        session.HookTokenHash.Should().NotBe(token);
        AgentHookToken.Matches(token, session.HookTokenHash).Should().BeTrue();

        _claude.LastContext!.Monitoring!.Endpoint.Should().Be(FakeAgentEventEndpoint.Listening);
        view.IsMonitored.Should().BeTrue();
        view.MonitoringNote.Should().BeNull();
    }

    [Fact]
    public async Task EverySession_HasItsOwnSecret()
    {
        Ready();
        await StartAsync();
        _processes.Exit(_sessions.Sessions[0].ProcessId!.Value);
        await StartAsync();

        _launcher.Launched.Select(launch => launch.Environment![AgentMonitoringEnvironment.HookToken])
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Start_WithMonitoringTurnedOff_OpensWithoutIt_AndRemembersTheChoice()
    {
        Ready();

        var view = await Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Monitor: false), Ct);

        view.IsMonitored.Should().BeFalse();
        view.MonitoringNote.Should().BeNull();
        _launcher.Launched[0].Environment.Should().BeNull();
        _claude.LastContext!.Monitoring.Should().BeNull();
        _settings.Monitoring["claude-code"].Should().BeFalse();
    }

    /// <summary>Acompanhar é um extra: sem porta local, o agente abre do mesmo jeito.</summary>
    [Fact]
    public async Task Start_WithoutTheLocalPort_OpensAnyway_AndSaysWhy()
    {
        Ready();
        _events.Address = null;

        var view = await StartAsync();

        view.Status.Should().Be(AgentSessionStatus.Running);
        view.IsMonitored.Should().BeFalse();
        view.MonitoringNote.Should().Contain("porta local");
    }

    [Fact]
    public async Task Start_WhenTheAgentsConfigurationBlocksHooks_OpensAnyway_AndSaysWhy()
    {
        Ready();
        _claude.MonitoringBlockedBy = "os hooks do Claude Code estão desligados.";

        var view = await StartAsync();

        view.Status.Should().Be(AgentSessionStatus.Running);
        view.IsMonitored.Should().BeFalse();
        view.MonitoringNote.Should().Contain("hooks do Claude Code estão desligados");
    }

    [Fact]
    public async Task Detect_BringsTheMonitoringChoice_OnByDefault()
    {
        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);
        status.Monitor.Should().BeTrue();

        _settings.Monitoring["claude-code"] = false;
        status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);
        status.Monitor.Should().BeFalse();
    }

    [Fact]
    public async Task TheProcessEnding_TakesItsWarningOffTheScreen()
    {
        var session = Persisted(15432, alive: false);

        await End().HandleAsync(new EndAgentSession(session.Id), Ct);

        _presenter.Dismissed.Should().Equal(session.Id);
    }

    // --- Terminal do agente (ADR-098) ----------------------------------------

    [Fact]
    public async Task InTheAgentsTerminal_TheSameStart_RunsInAPseudoTerminal_WithAKnownConversation()
    {
        Ready();
        var terminals = new FakeTerminalSessions(_processes, Now);
        _terminals = terminals;
        _settings.Arguments["claude-code"] = "--model opus";

        var view = await Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentWindow, RemoteOrigin: "Notebook"),
            Ct);

        view.Status.Should().Be(AgentSessionStatus.Running);
        view.Mode.Should().Be(AgentSessionMode.Terminal);
        view.RemoteOrigin.Should().Be("Notebook");
        _launcher.Launched.Should().BeEmpty("o terminal direto não abre");
        var (options, info) = terminals.Launched.Should().ContainSingle().Subject;
        options.Executable.Should().Be(FakeAgentCliProvider.Executable);
        info.SessionId.Should().Be(view.SessionId);
        info.Monitored.Should().BeTrue();
        _claude.LastContext!.Terminal.Should().Be(new AgentCliTerminal(_sessions.Sessions.Single().ExternalSessionId!, Resume: false));
        Guid.TryParse(_claude.LastContext.Terminal!.ConversationId, out _).Should().BeTrue();
        _claude.LastContext.Arguments.Should().Equal("--model", "opus");
    }

    [Fact]
    public async Task InTheAgentsTerminal_ResumingContinuesTheGivenConversation()
    {
        Ready();
        _terminals = new FakeTerminalSessions(_processes, Now);
        var conversation = Guid.NewGuid().ToString();

        await Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentEmbedded, ResumeConversation: conversation),
            Ct);

        _claude.LastContext!.Terminal.Should().Be(new AgentCliTerminal(conversation, Resume: true));
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("x; rm -rf /")]
    public async Task InTheAgentsTerminal_AConversationThatIsNotAnId_IsRefused(string conversation)
    {
        Ready();
        _terminals = new FakeTerminalSessions(_processes, Now);

        var start = () => Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentWindow, ResumeConversation: conversation),
            Ct);

        await start.Should().ThrowAsync<DomainException>().WithMessage("*não é válida*");
        _sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task OutsideTheAgent_TheAgentsTerminal_IsRefused_AndDirectStaysTheDefault()
    {
        Ready();

        var hosted = () => Start().HandleAsync(new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentWindow), Ct);

        await hosted.Should().ThrowAsync<DomainException>().WithMessage("*MyTaskApp Agent*");
        new StartAgentSession(_task.Id, _task.Developments[0].Id).Hosting.Should().Be(AgentHosting.Direct);

        var direct = await StartAsync();
        direct.Mode.Should().Be(AgentSessionMode.Interactive);
        _claude.LastContext!.Terminal.Should().BeNull("o terminal direto continua como era");
    }

    [Fact]
    public async Task WithoutAPseudoTerminal_TheAgentsTerminal_SaysWhy()
    {
        Ready();
        _terminals = new FakeTerminalSessions(_processes, Now) { Unsupported = "Sem pseudoterminal aqui." };

        var start = () => Start().HandleAsync(new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentWindow), Ct);

        await start.Should().ThrowAsync<DomainException>().WithMessage("Sem pseudoterminal aqui.");
    }

    [Fact]
    public async Task WhenThePseudoTerminalFails_TheSessionFails_WithTheReason()
    {
        Ready();
        _terminals = new FakeTerminalSessions(_processes, Now) { FailWith = "Este agente já tem 8 terminais abertos." };

        var view = await Start().HandleAsync(new StartAgentSession(_task.Id, _task.Developments[0].Id, Hosting: AgentHosting.AgentWindow), Ct);

        view.Status.Should().Be(AgentSessionStatus.Failed);
        view.FailureReason.Should().Contain("8 terminais");
    }

    [Fact]
    public async Task TheBypass_IsDetected_AndNeverChanged()
    {
        Ready();
        var terminals = new FakeTerminalSessions(_processes, Now);
        _terminals = terminals;
        _claude.Bypasses = true;

        await Start().HandleAsync(
            new StartAgentSession(_task.Id, _task.Developments[0].Id, Arguments: "--dangerously-skip-permissions", Hosting: AgentHosting.AgentWindow),
            Ct);

        terminals.Launched.Single().Info.PermissionsBypassed.Should().BeTrue();
        _claude.LastContext!.Arguments.Should().Equal("--dangerously-skip-permissions");
    }

    /// <summary>
    /// Sem escolha salva, "Abrir" continua o terminal direto de hoje (ADR-098):
    /// ninguém pede nada ao agente.
    /// </summary>
    [Fact]
    public async Task Open_WithoutAChoice_IsTheDirectTerminalAsBefore()
    {
        Ready();

        var view = await Open().HandleAsync(new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id)), Ct);

        view.IsAgentTerminal.Should().BeFalse();
        _launcher.Launched.Should().ContainSingle("o terminal direto abre aqui, como hoje");
        _agentTerminal.Started.Should().BeEmpty();
        _settings.Hostings.Should().BeEmpty("não escolher não grava escolha");
    }

    [Fact]
    public async Task Open_InTheAgentsWindow_AsksTheAgent_ShowsTheWindow_AndRemembers()
    {
        Ready();
        var session = HostedSession();
        _agentTerminal.Result = new TerminalStartResult(true, session.Id, null, null);

        var view = await Open().HandleAsync(
            new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id, Arguments: "--model opus", Prompt: "Revise"), AgentHosting.AgentWindow),
            Ct);

        view.SessionId.Should().Be(session.Id);
        view.IsAgentTerminal.Should().BeTrue();
        var request = _agentTerminal.Started.Should().ContainSingle().Subject;
        request.Hosting.Should().Be(AgentHosting.AgentWindow);
        request.Arguments.Should().Be("--model opus");
        request.Prompt.Should().Be("Revise");

        var window = _launcher.Launched.Should().ContainSingle("só a janela abre aqui; o Claude abre no agente").Subject;
        window.Arguments.Should().Contain(session.Id.ToString()).And.Contain("Claude Code · Implementar autenticação");
        _settings.Hostings[_claude.Id].Should().Be(AgentHosting.AgentWindow);

        // A próxima abertura sem escolha usa a salva.
        await Open().HandleAsync(new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id)), Ct);
        _agentTerminal.Started.Should().HaveCount(2).And.OnlyContain(started => started.Hosting == AgentHosting.AgentWindow);
    }

    [Fact]
    public async Task Open_InsideTheApp_OpensNoWindow()
    {
        Ready();
        var session = HostedSession();
        _agentTerminal.Result = new TerminalStartResult(true, session.Id, null, null);

        await Open().HandleAsync(new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id), AgentHosting.AgentEmbedded), Ct);

        _launcher.Launched.Should().BeEmpty();
        _agentTerminal.Started.Single().Hosting.Should().Be(AgentHosting.AgentEmbedded);
    }

    [Fact]
    public async Task Open_TheAgentsRefusal_IsTheSentenceThePersonReads()
    {
        Ready();
        _agentTerminal.Result = new TerminalStartResult(false, null, "Este ambiente já tem um Claude Code aberto.", null);

        var open = () => Open().HandleAsync(new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id), AgentHosting.AgentWindow), Ct);

        (await open.Should().ThrowAsync<DomainException>()).Which.Message.Should().Be("Este ambiente já tem um Claude Code aberto.");
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_AWindowThatDoesNotOpen_LeavesTheSessionShownWithTheReason()
    {
        Ready();
        var session = HostedSession();
        _agentTerminal.Result = new TerminalStartResult(true, session.Id, null, null);
        _launcher.FailWith = "Nenhum terminal encontrado.";

        var view = await Open().HandleAsync(new OpenAgentSession(new StartAgentSession(_task.Id, _task.Developments[0].Id), AgentHosting.AgentWindow), Ct);

        view.SessionId.Should().Be(session.Id);
        view.MonitoringNote.Should().Contain("Nenhum terminal encontrado.").And.Contain("segue rodando no agente");
    }

    [Fact]
    public async Task Focus_InTheAgentsTerminal_BringsTheWindow_OrOpensItAgainWhenClosed()
    {
        Ready();
        var session = HostedSession();

        (await Focus().HandleAsync(new FocusAgentSession(_task.Id, SessionId: session.Id), Ct)).Focused.Should().BeTrue();
        var first = _launcher.Launched.Should().ContainSingle("sem janela aberta, abre uma").Subject;

        (await Focus().HandleAsync(new FocusAgentSession(_task.Id, SessionId: session.Id), Ct)).Focused.Should().BeTrue();
        _launcher.Launched.Should().ContainSingle("a janela aberta vem para a frente");
        _windows.Focused.Should().ContainSingle();

        _processes.Exit(_windows.Focused[0]);
        await Focus().HandleAsync(new FocusAgentSession(_task.Id, SessionId: session.Id), Ct);
        _launcher.Launched.Should().HaveCount(2, "fechada a janela, abre outra; a sessão segue a mesma");
        first.Arguments.Should().Equal(_launcher.Launched[1].Arguments);
    }

    [Fact]
    public async Task TheAttention_LooksAtTheWindowOfTheView_NotAtTheClaudeProcess()
    {
        Ready();
        var session = HostedSession();

        _terminalWindows.WindowProcessFor(session.Id).Should().BeNull("nenhuma vista mostra a sessão ainda");

        await _terminalWindows.ShowAsync(session.Id, "Claude Code", Ct);
        _terminalWindows.WindowProcessFor(session.Id).Should().NotBeNull().And.NotBe(session.ProcessId);

        _processes.Exit(_terminalWindows.WindowProcessFor(session.Id)!.Value);
        _terminalWindows.SetEmbedded(session.Id, showing: true);
        _terminalWindows.WindowProcessFor(session.Id).Should().Be(Environment.ProcessId, "a vista embutida mora na janela do app");
    }

    [Fact]
    public async Task Detect_SaysWhereTheAgentOpens_AndOnlyOffersTheAgentWhereItExists()
    {
        _settings.Hostings[_claude.Id] = AgentHosting.AgentEmbedded;

        (await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct))
            .Hosting.Should().Be(AgentHosting.AgentEmbedded);

        _agentTerminal.Support = PseudoTerminalSupport.Unsupported("macOS", "Sem pseudoterminal.");
        var status = await new DetectAgentCliHandler(_providers, _settings, _agentTerminal).HandleAsync(new DetectAgentCli(), Ct);

        status.Hosting.Should().Be(AgentHosting.Direct);
        status.HostingSupport.IsSupported.Should().BeFalse();
    }

    /// <summary>A sessão que o agente "abriu", já no banco que os dois dividem.</summary>
    private AgentSession HostedSession()
    {
        var session = AgentSession.CreateTerminal(_task.Id, _task.Developments[0].Id, _claude.Id, _claude.Command, Worktree, Guid.NewGuid().ToString(), Now);
        session.MarkRunning(4242, Now);
        _processes.Run(4242, Now);
        _sessions.Seed(session);
        return session;
    }

    /// <summary>O agente hospedando: abre "pseudoterminais" que só ficam vivos no tracker.</summary>
    private sealed class FakeTerminalSessions(FakeAgentProcessTracker processes, DateTimeOffset now) : ITerminalSessions
    {
        public List<(TerminalLaunchOptions Options, TerminalSessionInfo Info)> Launched { get; } = [];

        public string? Unsupported { get; init; }

        public string? FailWith { get; init; }

        public bool IsHost => true;

        public PseudoTerminalSupport Support => Unsupported is null
            ? new PseudoTerminalSupport(true, "teste", null)
            : PseudoTerminalSupport.Unsupported("teste", Unsupported);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public TerminalSessionHost? Find(Guid sessionId) => null;

        public IReadOnlyList<TerminalSessionHost> List() => [];

        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default)
        {
            Launched.Add((options, info));

            if (FailWith is not null)
            {
                return Task.FromResult(TerminalLaunchResult.Failed(FailWith));
            }

            processes.Run(30001, now);
            return Task.FromResult(new TerminalLaunchResult { Started = true, ProcessId = 30001, ProcessStartedAt = now });
        }

        public event EventHandler<Guid>? ActivityChanged
        {
            add { }
            remove { }
        }

        public void NotifyActivity(Guid sessionId)
        {
        }

        public void Observe(AgentEvent agentEvent)
        {
        }
    }
}
