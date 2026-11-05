using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O Claude das outras máquinas na tarefa (ADR-103): o quadro lê o que os
/// agentes relatam, deixa esta máquina de fora, e cada pedido novo sai uma vez.
/// </summary>
public sealed class RemoteClaudeBoardTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeRemoteSettingsStore _settings = new(new RemoteSettings(true, false));

    private readonly FakeRemoteApi _remote = new();

    private readonly FakeRemoteWatch _watch = new() { IsConnected = true };

    private readonly ISyncAccount _account = Substitute.For<ISyncAccount>();

    private readonly IRemoteTerminalsApi _terminals = Substitute.For<IRemoteTerminalsApi>();

    private readonly IRemoteSessionsApi _sessions = Substitute.For<IRemoteSessionsApi>();

    private readonly Guid _here = Guid.CreateVersion7();

    private readonly Guid _notebook = Guid.CreateVersion7();

    private readonly Guid _task = Guid.CreateVersion7();

    private readonly Guid _development = Guid.CreateVersion7();

    private readonly ServiceProvider _services;

    private readonly RemoteClaudeBoard _board;

    private readonly List<RemoteClaudeState> _raised = [];

    private readonly List<Guid> _cleared = [];

    private List<RemoteTerminalSessionView> _reported = [];

    private List<RemoteAgentSessionView> _conversations = [];

    public RemoteClaudeBoardTests()
    {
        _account.IsAvailable.Returns(true);
        _remote.Devices.Add(Device(_here, "Desktop", isCurrent: true, executor: true, terminal: true));
        _remote.Devices.Add(Device(_notebook, "Notebook", isCurrent: false, executor: true, terminal: true));
        _terminals.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns(_ => new RemoteTerminalSessionList(_reported));
        _sessions.ListSessionsAsync(Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new RemoteAgentSessionList([new RemoteAgentPresence(_notebook, _time.GetUtcNow())], _conversations));

        _services = new ServiceCollection()
            .AddSingleton<IRemoteSettingsStore>(_settings)
            .AddSingleton<IRemoteApi>(_remote)
            .AddSingleton<ITrustedControllerRepository>(new FakeTrustedControllerRepository())
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton(_terminals)
            .AddSingleton(_sessions)
            .AddScoped<GetRemoteSettingsHandler>()
            .AddScoped<GetMachinesHandler>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        _board = new RemoteClaudeBoard(_services.GetRequiredService<IUseCaseRunner>(), _account, _watch, _time, NullLogger<RemoteClaudeBoard>.Instance);
        _board.AttentionRaised += (_, e) => _raised.Add(e.State);
        _board.AttentionCleared += (_, id) => _cleared.Add(id);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _board.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task TheStateOfAnotherMachine_ShowsOnTheTask_AndThisMachineStaysOut()
    {
        var there = Terminal(_notebook, RemoteAgentActivity.Working);
        var mine = Terminal(_here, RemoteAgentActivity.WaitingForUser);
        _reported = [there, mine];

        await _board.RefreshAsync(Ct);

        _board.ForTask(_task).Should().ContainSingle().Which.Should().Match<RemoteClaudeState>(state =>
            state.SessionId == there.Session.SessionId
            && state.MachineName == "Notebook"
            && state.Activity == RemoteAgentActivity.Working
            && state.Attention == RemoteClaudeAttention.None
            && state.CanFollow);
        RemoteClaudeTexts.Badge(_board.ForTask(_task)).Should().Be("● Claude em Notebook · trabalhando");
    }

    [Fact]
    public async Task WithTheRemoteOff_TheBoardIsEmpty_AndDoesNotHoldTheHub()
    {
        _settings.Settings = new RemoteSettings(false, false);
        _reported = [Terminal(_notebook, RemoteAgentActivity.Working)];

        await _board.RefreshAsync(Ct);

        _board.States.Should().BeEmpty();
        _board.IsListening.Should().BeFalse();
        _watch.Watchers.Should().Be(0);
        await _terminals.DidNotReceiveWithAnyArgs().ListTerminalsAsync(Ct);
    }

    [Fact]
    public async Task WithoutAnotherMachineAcceptingControl_TheBoardDoesNotHoldTheHub()
    {
        _remote.Devices.RemoveAll(device => device.DeviceId == _notebook);
        _remote.Devices.Add(Device(_notebook, "Notebook", isCurrent: false, executor: false, terminal: false));

        await _board.RefreshAsync(Ct);

        _board.IsListening.Should().BeFalse("quem tem uma máquina só não paga a conexão");
        _watch.Watchers.Should().Be(0);
        await _terminals.DidNotReceiveWithAnyArgs().ListTerminalsAsync(Ct);
    }

    [Fact]
    public async Task WithAnotherMachine_TheBoardListensToTheHub_AndStopReleasesIt()
    {
        await _board.RefreshAsync(Ct);
        _board.IsListening.Should().BeTrue();
        _watch.Watchers.Should().Be(1);

        await _board.StopAsync();

        _watch.Watchers.Should().Be(0);
    }

    [Fact]
    public async Task TheFirstRead_OnlyTakesNote()
    {
        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser)];

        await _board.RefreshAsync(Ct);

        _raised.Should().BeEmpty("o que já esperava antes de abrir não é novidade");
        _board.States.Should().ContainSingle().Which.NeedsAttention.Should().BeTrue("o selo mostra do mesmo jeito");
    }

    [Fact]
    public async Task ARequest_IsRaisedOnce_AndLeavingItClears()
    {
        var session = Guid.CreateVersion7();
        _reported = [Terminal(_notebook, RemoteAgentActivity.Working, session)];
        await _board.RefreshAsync(Ct);

        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser, session, message: "Posso rodar os testes?")];
        await _board.RefreshAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(30));
        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser, session, message: "Posso rodar os testes?", updatedAt: _time.GetUtcNow())];
        await _board.RefreshAsync(Ct);

        _raised.Should().ContainSingle("o relato novo do mesmo pedido não avisa de novo").Which.Should().Match<RemoteClaudeState>(state =>
            state.SessionId == session && state.Attention == RemoteClaudeAttention.Waiting && state.Message == "Posso rodar os testes?");
        RemoteClaudeTexts.Notice(_raised[0]).Should().Be("O Claude em Notebook espera você: Posso rodar os testes?");

        _reported = [Terminal(_notebook, RemoteAgentActivity.Working, session)];
        await _board.RefreshAsync(Ct);

        _cleared.Should().Equal(session);
    }

    [Fact]
    public async Task WaitingWorkingWaiting_AreTwoRequests_WithTheirOwnTime()
    {
        var session = Guid.CreateVersion7();
        _reported = [Terminal(_notebook, RemoteAgentActivity.Working, session)];
        await _board.RefreshAsync(Ct);

        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser, session)];
        await _board.RefreshAsync(Ct);
        var first = _board.States.Single().AttentionSince;

        _reported = [Terminal(_notebook, RemoteAgentActivity.Working, session)];
        await _board.RefreshAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser, session)];
        await _board.RefreshAsync(Ct);

        _raised.Should().HaveCount(2);
        _board.States.Single().AttentionSince.Should().NotBe(first, "o pedido novo pulsa de novo na linha");
    }

    [Fact]
    public async Task TheSessionThatEnds_ClearsItsRequest()
    {
        var session = Guid.CreateVersion7();
        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingReview, session)];
        await _board.RefreshAsync(Ct);

        _reported = [];
        await _board.RefreshAsync(Ct);

        _cleared.Should().Equal(session);
        _board.States.Should().BeEmpty();
    }

    [Fact]
    public async Task AMachineWithoutTheRemoteTerminal_ShowsTheState_ButCannotBeFollowed()
    {
        _remote.Devices.RemoveAll(device => device.DeviceId == _notebook);
        _remote.Devices.Add(Device(_notebook, "Notebook", isCurrent: false, executor: true, terminal: false));
        _reported = [Terminal(_notebook, RemoteAgentActivity.WaitingForUser, message: null)];

        await _board.RefreshAsync(Ct);

        var state = _board.States.Single();
        state.CanFollow.Should().BeFalse();
        state.Message.Should().BeNull();
        RemoteClaudeTexts.Action(state).Should().Be("Notebook mostra o estado, mas não aceita o terminal remoto.");
    }

    [Fact]
    public async Task AConversation_AnsweredBetweenTwoReads_IsANewRequest()
    {
        var session = Guid.CreateVersion7();
        _conversations = [Conversation(session, RemoteAgentSessionStatus.Idle, operation: Guid.CreateVersion7(), version: 1)];
        await _board.RefreshAsync(Ct);

        // Um prompt inteiro entre duas leituras: o estado volta ao mesmo "terminou", com outra operação.
        _conversations = [Conversation(session, RemoteAgentSessionStatus.Idle, operation: Guid.CreateVersion7(), version: 4)];
        await _board.RefreshAsync(Ct);

        _raised.Should().ContainSingle().Which.Should().Match<RemoteClaudeState>(state =>
            state.Kind == RemoteClaudeKind.Conversation && state.Attention == RemoteClaudeAttention.Review && state.Version == 4 && !state.CanFollow);
        RemoteClaudeTexts.Short(_raised[0]).Should().Be("esperando o próximo prompt");
    }

    [Fact]
    public async Task OldConversations_AndThoseOfAStoppedAgent_StayOut()
    {
        _conversations =
        [
            Conversation(Guid.CreateVersion7(), RemoteAgentSessionStatus.Idle, updatedAt: _time.GetUtcNow() - TimeSpan.FromDays(2)),
            Conversation(Guid.CreateVersion7(), RemoteAgentSessionStatus.Failed, updatedAt: _time.GetUtcNow() - TimeSpan.FromHours(3)),
            Conversation(Guid.CreateVersion7(), RemoteAgentSessionStatus.Stopped),
        ];
        await _board.RefreshAsync(Ct);
        _board.States.Should().BeEmpty();

        _conversations = [Conversation(Guid.CreateVersion7(), RemoteAgentSessionStatus.Running)];
        _sessions.ListSessionsAsync(Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new RemoteAgentSessionList([new RemoteAgentPresence(_notebook, _time.GetUtcNow() - TimeSpan.FromMinutes(10))], _conversations));
        await _board.RefreshAsync(Ct);

        _board.States.Should().BeEmpty("com o agente parado, a conversa não anda");
    }

    [Fact]
    public async Task AnOldServer_GivesNothing_WithoutFailing()
    {
        _terminals.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns<RemoteTerminalSessionList>(_ => throw new SyncApiException(SyncFailureKind.NotFound, "404"));
        _sessions.ListSessionsAsync(Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<RemoteAgentSessionList>(_ => throw new SyncApiException(SyncFailureKind.NotFound, "404"));

        await _board.RefreshAsync(Ct);

        _board.States.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheServer_TheBoardKeepsWhatItSaw()
    {
        _reported = [Terminal(_notebook, RemoteAgentActivity.Working)];
        await _board.RefreshAsync(Ct);

        _terminals.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns<RemoteTerminalSessionList>(_ => throw new SyncApiException(SyncFailureKind.Transient, "sem rede"));
        await _board.RefreshAsync(Ct);

        _board.States.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyAChange_RaisesChanged()
    {
        var changes = 0;
        _board.Changed += (_, _) => changes++;
        _reported = [Terminal(_notebook, RemoteAgentActivity.Working)];

        await _board.RefreshAsync(Ct);
        await _board.RefreshAsync(Ct);

        changes.Should().Be(1);
    }

    [Fact]
    public async Task WithSeveral_TheBadgeShowsTheMostUrgent()
    {
        _reported =
        [
            Terminal(_notebook, RemoteAgentActivity.Working),
            Terminal(_notebook, RemoteAgentActivity.WaitingForUser),
        ];

        await _board.RefreshAsync(Ct);

        RemoteClaudeTexts.Badge(_board.ForTask(_task)).Should().Be("⚠ Claude em Notebook ×2 · aguardando você");
        _board.States[0].Attention.Should().Be(RemoteClaudeAttention.Waiting, "o que mais pede alguém vem primeiro");
    }

    private RemoteTerminalSessionView Terminal(
        Guid deviceId,
        RemoteAgentActivity activity,
        Guid? sessionId = null,
        string? message = "texto",
        DateTimeOffset? updatedAt = null) =>
        new(
            deviceId,
            new RemoteTerminalSessionReport(
                sessionId ?? Guid.CreateVersion7(),
                _task,
                _development,
                RemoteTerminalProcess.Running,
                null,
                activity,
                message,
                HasDialog: false,
                Monitored: true,
                PermissionsBypassed: false,
                RemotelyControlled: false,
                _time.GetUtcNow(),
                updatedAt ?? _time.GetUtcNow()),
            _time.GetUtcNow());

    private RemoteAgentSessionView Conversation(
        Guid sessionId,
        RemoteAgentSessionStatus status,
        Guid? operation = null,
        long version = 1,
        DateTimeOffset? updatedAt = null) =>
        new(
            sessionId,
            _notebook,
            version,
            _task,
            _development,
            "padrao",
            null,
            status,
            RemoteAgentActivity.Unknown,
            null,
            operation,
            _time.GetUtcNow(),
            status == RemoteAgentSessionStatus.Stopped ? _time.GetUtcNow() : null,
            updatedAt ?? _time.GetUtcNow(),
            _time.GetUtcNow());

    private RemoteDeviceInfo Device(Guid id, string nickname, bool isCurrent, bool executor, bool terminal) =>
        new(
            id,
            nickname,
            "windows",
            "1.0",
            isCurrent,
            executor ? DeviceRoles.Controller | DeviceRoles.Executor : DeviceRoles.Controller,
            null,
            new DeviceCapabilities(
                RemoteLimits.ProtocolVersion,
                terminal ? [RemoteOperationKinds.TerminalAttach] : [],
                "windows",
                ToolStatus.Missing,
                ToolStatus.Missing,
                ToolStatus.Missing,
                ToolStatus.Missing,
                1,
                string.Empty),
            _time.GetUtcNow());
}
