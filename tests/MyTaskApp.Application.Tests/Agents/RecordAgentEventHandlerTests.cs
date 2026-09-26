using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>
/// O aviso do agente chega à sessão que o abriu (ADR-036): associação
/// conferida pelo segredo, atividade mudada, e o usuário avisado quando o
/// agente para esperando por ele.
/// </summary>
public class RecordAgentEventHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private const int ProcessId = 15432;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly RecordingAgentSessionWatcher _watcher = new();
    private readonly RecordingAgentAttentionPresenter _presenter = new();
    private readonly FakeTerminalWindowManager _windows = new();
    private readonly TaskItem _task;
    private readonly AgentSession _session;
    private readonly string _token;

    public RecordAgentEventHandlerTests()
    {
        _task = TaskItem.Create("Implementar cache", Now);
        var development = _task.BeginDevelopment(null, @"C:\Projects\eco-core", "origin/main", "feature/123", Worktree, Now);
        _task.MarkDevelopmentReady(development.Id, Now);
        _tasks.Seed(_task);

        (_token, var hash) = AgentHookToken.Create();

        _session = AgentSession.Create(_task.Id, development.Id, "claude-code", @"C:\claude.exe", Worktree, Now);
        _session.EnableMonitoring(hash);
        _session.MarkRunning(ProcessId, Now);
        _sessions.Seed(_session);
    }

    private RecordAgentEventHandler Handler() =>
        new(
            _sessions,
            _tasks,
            _tasks,
            new AgentCliProviders([new FakeAgentCliProvider()]),
            _watcher,
            _presenter,
            _windows,
            NullLogger<RecordAgentEventHandler>.Instance);

    private AgentEvent Event(
        AgentEventType type,
        string? message = null,
        Guid? sessionId = null,
        Guid? taskId = null,
        string? cwd = Worktree) =>
        new(type, sessionId ?? _session.Id, taskId ?? _task.Id, "claude-abc", cwd, message, Now.AddMinutes(3), type.ToString());

    private Task<AgentEventOutcome> SendAsync(AgentEvent agentEvent, string? token = null) =>
        Handler().HandleAsync(new RecordAgentEvent(agentEvent, token ?? _token), Ct);

    [Fact]
    public async Task AQuestion_PutsTheSessionWaiting_AndTellsTheUser()
    {
        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput, "Redis ou MemoryCache?"));

        outcome.Should().Be(AgentEventOutcome.Applied);
        _session.Activity.Should().Be(AgentActivity.WaitingForUser);
        _session.ExternalSessionId.Should().Be("claude-abc");
        _watcher.Changes.Should().Equal(_task.Id);

        var attention = _presenter.Presented.Should().ContainSingle().Subject;
        attention.SessionId.Should().Be(_session.Id);
        attention.TaskTitle.Should().Be("Implementar cache");
        attention.AgentName.Should().Be("Claude Code");
        attention.RepositoryName.Should().Be("eco-core");
        attention.Branch.Should().Be("feature/123");
        attention.Activity.Should().Be(AgentActivity.WaitingForUser);
        attention.Message.Should().Be("Redis ou MemoryCache?");
    }

    /// <summary>"Stop" é o fim de uma resposta, não o da sessão: aguardando revisão.</summary>
    [Fact]
    public async Task AFinishedResponse_WaitsForReview_AndTheSessionStaysOpen()
    {
        await SendAsync(Event(AgentEventType.ResponseCompleted, "Implementação concluída."));

        _session.Activity.Should().Be(AgentActivity.WaitingReview);
        _session.Status.Should().Be(AgentSessionStatus.Running);
        _presenter.Presented.Should().ContainSingle();
    }

    [Fact]
    public async Task AFailedResponse_TellsTheUser()
    {
        await SendAsync(Event(AgentEventType.SessionFailed, "Limite de uso atingido."));

        _session.Activity.Should().Be(AgentActivity.Failed);
        _presenter.Presented.Should().ContainSingle();
    }

    [Fact]
    public async Task BackToWork_TakesTheWarningOffTheScreen()
    {
        await SendAsync(Event(AgentEventType.NeedsUserInput));

        await SendAsync(Event(AgentEventType.Working));

        _session.Activity.Should().Be(AgentActivity.Working);
        _presenter.Dismissed.Should().Equal(_session.Id);
    }

    [Fact]
    public async Task TheSameEventTwice_TellsTheUserOnce()
    {
        await SendAsync(Event(AgentEventType.ResponseCompleted));

        var second = await SendAsync(Event(AgentEventType.ResponseCompleted));

        second.Should().Be(AgentEventOutcome.Ignored);
        _presenter.Presented.Should().ContainSingle();
    }

    /// <summary>Quem está olhando para o terminal já sabe: nada de aviso por cima da conversa.</summary>
    [Fact]
    public async Task WithTheTerminalInFront_TheStateChanges_ButNoWarningShows()
    {
        _windows.InFront.Add(ProcessId);

        await SendAsync(Event(AgentEventType.ResponseCompleted));

        _session.Activity.Should().Be(AgentActivity.WaitingReview);
        _presenter.Presented.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentEventType.SessionStopped)]
    [InlineData(AgentEventType.TaskCompleted)]
    [InlineData(AgentEventType.Notification)]
    public async Task InformativeEvents_DoNotChangeTheActivity(AgentEventType type)
    {
        await SendAsync(Event(AgentEventType.Working));

        await SendAsync(Event(type, "qualquer coisa"));

        _session.Activity.Should().Be(AgentActivity.Working);
        _session.Status.Should().Be(AgentSessionStatus.Running);
    }

    [Fact]
    public async Task SessionStarted_OnlyRemembersTheAgentsSessionId()
    {
        var outcome = await SendAsync(Event(AgentEventType.SessionStarted));

        outcome.Should().Be(AgentEventOutcome.Ignored);
        _session.ExternalSessionId.Should().Be("claude-abc");
        _session.Activity.Should().Be(AgentActivity.Unknown);
        _tasks.SaveCount.Should().Be(1);
    }

    // --- A associação é conferida, não confiada ------------------------------

    [Fact]
    public async Task AWrongSecret_IsRejected()
    {
        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput), token: AgentHookToken.Create().Token);

        outcome.Should().Be(AgentEventOutcome.Rejected);
        _session.Activity.Should().Be(AgentActivity.Unknown);
        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownSession_IsRejected()
    {
        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput, sessionId: Guid.CreateVersion7()));

        outcome.Should().Be(AgentEventOutcome.Rejected);
    }

    [Fact]
    public async Task ATaskThatIsNotTheSessions_IsRejected()
    {
        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput, taskId: Guid.CreateVersion7()));

        outcome.Should().Be(AgentEventOutcome.Rejected);
    }

    /// <summary>Sessão aberta sem acompanhamento não tem segredo — nenhum aviso vale.</summary>
    [Fact]
    public async Task AnUnmonitoredSession_AcceptsNothing()
    {
        var plain = AgentSession.Create(_task.Id, Guid.CreateVersion7(), "claude-code", @"C:\claude.exe", Worktree, Now);
        plain.MarkRunning(999, Now);
        _sessions.Seed(plain);

        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput, sessionId: plain.Id));

        outcome.Should().Be(AgentEventOutcome.Rejected);
    }

    [Fact]
    public async Task AnEndedSession_IgnoresLateEvents()
    {
        _session.MarkExited(Now.AddMinutes(1));

        var outcome = await SendAsync(Event(AgentEventType.NeedsUserInput));

        outcome.Should().Be(AgentEventOutcome.Ignored);
        _presenter.Presented.Should().BeEmpty();
    }

    /// <summary>A pasta não decide nada — o segredo decide. Fora do worktree só vai para o log.</summary>
    [Fact]
    public async Task AFolderOutsideTheWorktree_StillCounts()
    {
        var outcome = await SendAsync(Event(AgentEventType.Working, cwd: @"C:\outro"));

        outcome.Should().Be(AgentEventOutcome.Applied);
    }
}
