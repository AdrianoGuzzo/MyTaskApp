using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>O aviso do canto, no desktop, para o Claude de outra máquina (ADR-103).</summary>
public sealed class RemoteClaudeAnnouncerTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private readonly IAgentAttentionPresenter _presenter = Substitute.For<IAgentAttentionPresenter>();

    private readonly ITaskItemRepository _tasks = Substitute.For<ITaskItemRepository>();

    private readonly IAudioPlayer _audio = Substitute.For<IAudioPlayer>();

    private readonly ServiceProvider _services;

    private readonly RemoteClaudeAnnouncer _announcer;

    public RemoteClaudeAnnouncerTests()
    {
        var sounds = Substitute.For<IAgentAlertSoundStore>();
        sounds.GetAsync(Arg.Any<CancellationToken>()).Returns([]);
        var library = Substitute.For<ISoundLibrary>();
        library.Resolve(Arg.Any<string>()).Returns(@"C:\sons\chamada.wav");

        _services = new ServiceCollection()
            .AddSingleton(_tasks)
            .AddSingleton(sounds)
            .AddSingleton(library)
            .AddSingleton(_audio)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddScoped<AgentAlertSoundPlayer>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var board = new RemoteClaudeBoard(
            _services.GetRequiredService<IUseCaseRunner>(),
            Substitute.For<ISyncAccount>(),
            new NoRemoteWatch(),
            _time,
            NullLogger<RemoteClaudeBoard>.Instance);

        _announcer = new RemoteClaudeAnnouncer(board, _services.GetRequiredService<IUseCaseRunner>(), _presenter, NullLogger<RemoteClaudeAnnouncer>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task TheAlert_NamesTheMachine_AndPlaysTheSoundOfTheState()
    {
        var task = TaskItem.Create("Corrigir o login", _time.GetUtcNow());
        _tasks.FindByIdAsync(task.Id, Arg.Any<CancellationToken>()).Returns(task);
        var state = State(task.Id, RemoteClaudeAttention.Waiting, canFollow: true);

        await _announcer.AnnounceAsync(state, Ct);

        await _presenter.Received(1).PresentAsync(
            Arg.Is<AgentAttention>(attention =>
                attention.SessionId == state.SessionId
                && attention.TaskTitle == "Corrigir o login"
                && attention.Activity == AgentActivity.WaitingForUser
                && attention.Remote != null
                && attention.Remote.MachineName == "Notebook"
                && attention.Remote.CanFollow
                && !attention.Remote.IsConversation),
            Arg.Any<CancellationToken>());
        _audio.Received(1).Play(Arg.Any<string>());
    }

    [Fact]
    public async Task ATaskThatHasNotArrived_StillAlerts()
    {
        var state = State(Guid.CreateVersion7(), RemoteClaudeAttention.Failed, canFollow: false);

        await _announcer.AnnounceAsync(state, Ct);

        await _presenter.Received(1).PresentAsync(
            Arg.Is<AgentAttention>(attention => attention.TaskTitle == "Tarefa" && attention.Activity == AgentActivity.Failed && !attention.Remote!.CanFollow),
            Arg.Any<CancellationToken>());
    }

    private RemoteClaudeState State(Guid taskId, RemoteClaudeAttention attention, bool canFollow) => new(
        Guid.CreateVersion7(),
        "Notebook",
        Guid.CreateVersion7(),
        taskId,
        null,
        RemoteClaudeKind.Terminal,
        attention == RemoteClaudeAttention.Waiting ? RemoteAgentActivity.WaitingForUser : RemoteAgentActivity.Failed,
        HasDialog: false,
        "Posso apagar?",
        canFollow,
        attention,
        _time.GetUtcNow());
}
