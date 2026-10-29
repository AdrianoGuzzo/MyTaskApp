using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>
/// O aviso "aberto de…" (ADR-092): a conversa que outro dispositivo abre nesta
/// máquina aparece no canto, uma vez, assim que o agente avisa que gravou.
/// </summary>
public sealed class RemoteSessionAnnouncerTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly RecordingAgentAttentionPresenter _presenter = new();
    private readonly DataChangeNotifier _notifier = new();
    private readonly TaskItem _task = TaskItem.Create("Implementar cache", Now);
    private readonly ServiceProvider _services;

    public RemoteSessionAnnouncerTests()
    {
        _task.BeginDevelopment(null, @"C:\Projects\eco-core", "origin/main", "feature/123", Worktree, Now);
        _tasks.Seed(_task);

        _services = new ServiceCollection()
            .AddSingleton<IAgentSessionRepository>(_sessions)
            .AddSingleton<ITaskItemRepository>(_tasks)
            .AddSingleton<IAgentCliProviders>(new AgentCliProviders([new FakeAgentCliProvider()]))
            .AddScoped<GetRemotelyOpenedSessionsHandler>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private RemoteSessionAnnouncer Announcer() =>
        new(_notifier, _services.GetRequiredService<IUseCaseRunner>(), _presenter, _time, NullLogger<RemoteSessionAnnouncer>.Instance);

    private AgentSession Open(string? origin, DateTimeOffset at)
    {
        var session = AgentSession.CreateHeadless(
            _task.Id, _task.Developments[0].Id, "claude-code", "claude", Worktree,
            Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null, null, at, origin);
        _sessions.Seed(session);
        return session;
    }

    [Fact]
    public async Task AConversationOpenedFromAnotherDevice_IsAnnouncedOnce_WithWhoOpenedIt()
    {
        var announcer = Announcer();
        announcer.Start();
        _time.Advance(TimeSpan.FromMinutes(1));
        var session = Open("Pixel 8", _time.GetUtcNow());

        await announcer.CheckAsync(Ct);
        await announcer.CheckAsync(Ct);

        var attention = _presenter.Presented.Should().ContainSingle("cada conversa é avisada uma vez").Subject;
        attention.SessionId.Should().Be(session.Id);
        attention.OpenedBy.Should().Be("Pixel 8");
        attention.TaskTitle.Should().Be("Implementar cache");
        attention.AgentName.Should().Be("Claude Code");
        attention.Branch.Should().Be("feature/123");
    }

    [Fact]
    public async Task ConversationsFromBeforeTheStart_OrOpenedHere_AreNotAnnounced()
    {
        Open("Pixel 8", Now.AddMinutes(-5));
        var announcer = Announcer();
        announcer.Start();
        _time.Advance(TimeSpan.FromMinutes(1));
        Open(null, _time.GetUtcNow());

        await announcer.CheckAsync(Ct);

        _presenter.Presented.Should().BeEmpty("as de antes estão no card, e a aberta daqui não precisa de aviso");
    }

    [Fact]
    public async Task TheAgentWarningThatItSaved_TriggersTheCheck()
    {
        var announcer = Announcer();
        announcer.Start();
        _time.Advance(TimeSpan.FromMinutes(1));
        Open("Notebook", _time.GetUtcNow());

        _notifier.NotifyChanged(DataArea.Tasks);
        _presenter.Presented.Should().BeEmpty("outra área não é conversa");

        _notifier.NotifyChanged(DataArea.AgentSessions);

        for (var attempt = 0; attempt < 100 && _presenter.Presented.Count == 0; attempt++)
        {
            await Task.Delay(10, Ct);
        }

        _presenter.Presented.Should().ContainSingle().Which.OpenedBy.Should().Be("Notebook");
    }
}
